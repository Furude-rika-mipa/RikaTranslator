'use strict';

const scriptUi = {
  job: '',
  game: '',
  file: '',
  scene: '',
  query: '',
  failedOnly: false,
  page: 1,
  selected: new Set(),
  jobs: [],
  status: null,
  rows: null,
  pool: null,
  mcp: null,
  method: 'api',
};
let scriptPoll = null;
let scriptPageSequence = 0;
const scriptStage = (s) =>
  ({
    extracting: '正在提取',
    parsing: '识别对白',
    ready: '等待选择',
    translating: '正在翻译',
    external_waiting: '等待外部助手',
    paused: '已暂停',
    complete: '本次已完成',
    failed: '提取失败',
    cancelled: '已取消',
    unsupported: '暂不支持此剧本',
    interrupted: '提取已中断',
  })[s] || s;
const scriptLineStage = (s) =>
  ({ pending: '待翻译', queued: '本次队列', translated: '已完成', failed: '可重试', skipped: '仅标点' })[s] || s;

async function renderScriptTranslation(sequence = renderSequence) {
  clearTimeout(scriptPoll);
  const [jobs, pool, mcp] = await Promise.all([
    api('/api/script-translation/jobs'),
    api('/api/script-translation/settings'),
    api('/api/script-translation/mcp/settings'),
  ]);
  if (sequence !== renderSequence) return;
  scriptUi.pool = pool;
  scriptUi.mcp = mcp;
  scriptUi.jobs = jobs;
  const requested = state.gameId;
  if (requested && jobs.some((j) => j.id === requested) && scriptUi.job !== requested) {
    scriptUi.job = requested;
    scriptUi.selected.clear();
    scriptUi.file = '';
    scriptUi.scene = '';
    scriptUi.failedOnly = false;
    scriptUi.page = 1;
  }
  if (!jobs.some((j) => j.id === scriptUi.job)) scriptUi.job = jobs[0]?.id || '';
  const games = state.games.filter((g) =>
    ['KiriKiri', 'WillPlus', 'YU-RIS', 'ExHIBIT', 'Escu:de', 'SiglusEngine', 'BGI / Ethornell', 'Unknown'].includes(
      g.engine,
    ),
  );
  if (!games.some((g) => g.id === scriptUi.game)) scriptUi.game = games[0]?.id || '';
  const extractForm = games.length
    ? `<form id="script-extract-form">
        <label class="field">游戏<select name="gameId">${games
          .map(
            (g) =>
              `<option value="${esc(g.id)}" ${g.id === scriptUi.game ? 'selected' : ''}>${esc(g.name)} · ${esc(g.engine)}</option>`,
          )
          .join('')}</select></label>
        <label class="field">剧本资源<select name="archive" disabled><option>正在检查剧本资源…</option></select>
          <small>支持已适配的 KiriKiri / Hxv4、WillPlus / AdvHD、YU-RIS、ExHIBIT、Escu:de、SiglusEngine、BGI / Ethornell 格式。实际范围以资源检查结果为准。</small>` +
      `</label>
        <p id="script-extract-hint" class="tiny subtle" role="status"></p>
        <div class="form-actions"><button class="btn primary" type="submit" disabled>${icon('play')}提取剧本</button>` +
      `</div>
      </form>`
    : notice('请先在游戏库添加游戏，再回到这里选择。<a href="#library">打开游戏库</a>');
  main.innerHTML =
    `${pageHead('剧本翻译', '选择剧本范围，使用 AI 服务或外部助手翻译。', '', 'STORY TRANSLATION')}
    <div class="script-welcome">
      <span class="script-welcome-icon">${icon('ai')}</span>
      <div><strong>让故事，慢慢被读懂。</strong><p>提取剧本后，选择想翻译的对白或章节。</p></div>
      <button class="btn script-welcome-action" type="button" id="script-open-setup">${icon('plus')}提取新剧本</button>
    </div>
    <details class="card script-setup-disclosure section-gap" id="script-setup" ${jobs.length ? '' : 'open'}>
      <summary><span>${icon('book')}提取剧本</span><small>选择游戏和剧本资源</small></summary>
      <div class="grid-two script-setup-content"><section>${extractForm}</section><section>
        <h3>使用方式</h3>
        <p class="card-description">支持已适配的 KiriKiri / Hxv4、WillPlus / AdvHD、YU-RIS、ExHIBIT、Escu:de、SiglusEngine 和 BGI / Ethornell 剧本。</p>
        <p class="card-description section-gap">KiriKiri / Hxv4 使用临时游戏副本提取，其他上述引擎离线读取。游戏的资源格式和版本仍需通过检查；提取后不会自动调用 AI。</p>
        <p class="card-description section-gap">默认不选择对白。可勾选最多 1000 条，也可预览并确认当前剧本或全部待译。</p>
        <p class="tiny subtle section-gap">已完成项自动跳过，中断后可继续；可导出译文或为已适配引擎生成汉化补丁。</p>
      </section></div>
    </details>
    <section class="card section-gap" id="script-external-panel"></section>
    <section class="card section-gap"><div class="card-header"><h2>${icon('clock')}任务记录</h2><button class="btn ghost small" data-script="refresh">` +
    `${icon('refresh')}刷新</button>` +
    `</div><div id="script-job-list"></div></section>
    <section class="card section-gap" id="script-workspace"></section>
    <section class="card section-gap" id="script-pool-panel"></section>`;
  document.getElementById('script-open-setup').onclick = () => {
    const setup = document.getElementById('script-setup');
    setup.open = true;
    const behavior = matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth';
    setup.scrollIntoView({ behavior, block: 'start' });
  };
  if (jobs.find((j) => j.id === scriptUi.job)?.external?.enabled) scriptUi.method = 'mcp';
  drawScriptPool();
  drawScriptExternalPanel();
  drawScriptJobs();
  const form = document.getElementById('script-extract-form');
  if (form) {
    form.elements.gameId.onchange = () => {
      scriptUi.game = form.elements.gameId.value;
      void loadScriptArchives(form);
    };
    form.onsubmit = async (event) => {
      event.preventDefault();
      await busy(form.querySelector('[type=submit]'), async () => {
        const job = await api('/api/script-translation/extract', {
          method: 'POST',
          body: { gameId: form.elements.gameId.value, archive: form.elements.archive.value },
        });
        scriptUi.job = job.id;
        scriptUi.selected.clear();
        scriptUi.file = '';
        scriptUi.scene = '';
        scriptUi.failedOnly = false;
        scriptUi.page = 1;
        scriptUi.jobs.unshift(job);
        drawScriptJobs();
        await drawScriptWorkspace(job);
        scheduleScriptPoll();
      });
      if (form.isConnected)
        form.querySelector('[type=submit]').disabled =
          form.elements.archive.disabled || scriptUi.jobs.some((j) => j.busy);
    };
    await loadScriptArchives(form);
  }
  if (sequence !== renderSequence) return;
  if (scriptUi.job) await drawScriptWorkspace(await api(`/api/script-translation/jobs/${scriptUi.job}`));
  else
    document.getElementById('script-workspace').innerHTML =
      '<p class="card-description">尚未提取剧本。提取完成后，在这里预览并勾选少量对白。</p>';
  scheduleScriptPoll();
}

async function loadScriptArchives(form) {
  const id = form.elements.gameId.value;
  const sequence = (form._inspection = (form._inspection || 0) + 1);
  const select = form.elements.archive,
    start = form.querySelector('[type=submit]'),
    hint = document.getElementById('script-extract-hint');
  start.disabled = true;
  select.disabled = true;
  try {
    const data = await api(`/api/script-translation/games/${encodeURIComponent(id)}`);
    if (!form.isConnected || sequence !== form._inspection) return;
    select.innerHTML = data.archives.length
      ? data.archives
          .map(
            (a) =>
              `<option value="${esc(a.name)}" ${a.name.toLowerCase() === 'data.xp3' ? 'selected' : ''}>${a.name === 'Archive' ? 'Archive · 全部受支持剧本' : esc(a.name)} · ${esc(updateSize(a.bytes))}</option>`,
          )
          .join('')
      : '<option value="">没有找到受支持的剧本资源</option>';
    start.innerHTML = `${icon('play')}${data.offline ? '离线提取剧本' : '启动副本并提取'}`;
    hint.textContent = data.message;
    select.disabled = !data.supported || !data.archives.length;
    start.disabled = select.disabled || scriptUi.jobs.some((j) => j.busy);
  } catch (error) {
    if (form.isConnected && sequence === form._inspection) {
      select.innerHTML = '<option value="">无法读取资源包</option>';
      hint.textContent = error.message;
    }
  }
}

function drawScriptJobs() {
  const holder = document.getElementById('script-job-list');
  if (!holder) return;
  const previousList = holder.querySelector('.script-job-list');
  const previousSelection = previousList?.querySelector('.script-job.selected')?.dataset.id;
  const previousScroll = previousList?.scrollTop || 0;
  holder.innerHTML = scriptUi.jobs.length
    ? `<div class="script-job-list">${scriptUi.jobs
        .map(
          (j) =>
            `<button class="script-job ${scriptUi.job === j.id ? 'selected' : ''}" data-script="job" data-id="${esc(j.id)}">` +
            `<span><strong>${esc(j.gameName)}</strong>` +
            `<small>${esc(j.archive)} · ${esc(dateText(j.createdAt))}</small>` +
            `</span>${badge(scriptStage(j.stage), j.busy ? 'blue' : j.stage === 'failed' ? 'red' : '')}<span class="tiny subtle">` +
            `${number(j.translated)} / ${number(j.total)} 条已译</span>` +
            `</button>`,
        )
        .join('')}</div>`
    : '<p class="card-description">尚无任务。</p>';
  const list = holder.querySelector('.script-job-list');
  const selected = list?.querySelector('.script-job.selected');
  if (list && selected) {
    if (previousSelection === scriptUi.job) list.scrollTop = previousScroll;
    else {
      const listRect = list.getBoundingClientRect(),
        selectedRect = selected.getBoundingClientRect();
      if (selectedRect.bottom > listRect.bottom) list.scrollTop += selectedRect.bottom - listRect.bottom;
    }
  }
  const form = document.getElementById('script-extract-form');
  if (form)
    form.querySelector('[type=submit]').disabled = form.elements.archive.disabled || scriptUi.jobs.some((j) => j.busy);
  updateScriptPoolDisabled();
  updateScriptExternalControls();
}

function drawScriptPool() {
  const holder = document.getElementById('script-pool-panel');
  if (!holder) return;
  const pool = scriptUi.pool || { multiProvider: false, providers: [] };
  holder.innerHTML =
    `<div class="card-header"><h2>${icon('ai')}多 API 分担</h2></div><form id="script-pool-form">
    <label class="script-pool-toggle"><input type="checkbox" name="multiProvider" ${pool.multiProvider ? 'checked' : ''}>` +
    `启用多 API 分担</label>
    <p class="card-description section-gap">仅影响剧本批量翻译；未启用时使用当前游戏的 AI 服务。启用后，把本次选中的内容分配给下面勾选的服务，分别控制并发。</p>
    <label class="field section-gap">API 每批最多对白数<input type="number" name="batchLines" min="1" max="50" step="1" required value="${number(pool.batchLines || 8)}"><small>1–50 条，默认 8 条。单 API 和多 API 剧本翻译均适用；长对白、场景切换或总字数限制可能使实际条数更少。批次过大可能增加超时或格式错误的概率。</small></label>
    <div class="script-pool-list section-gap">${
      state.providers.length
        ? state.providers
            .map((p) => {
              const saved = pool.providers.find((v) => v.providerId === p.id);
              return (
                `<div class="script-pool-row" data-script-provider="${esc(p.id)}"><label><input type="checkbox" data-pool-select ${saved ? 'checked' : ''}>` +
                `<span><strong>${esc(p.name)}</strong>` +
                `<small>${esc(p.model || '尚未填写模型')}</small>` +
                `</span></label><label class="script-pool-concurrency">并发<input type="number" data-pool-concurrency value="${saved?.concurrency || 1}" min="1" max="8" step="1" aria-label="${esc(p.name)}的并发数">` +
                `</label></div>`
              );
            })
            .join('')
        : notice('请先在 <a href="#ai">AI 翻译</a> 中添加服务。')
    }</div>
    <p class="tiny subtle section-gap">最多选择 8 个 API，每个并发 1–8，总并发最多 16。超时、限流或服务暂不可用时尝试其他参与 API；配置错误的服务会在本次任务中停用。</p>
    <p class="tiny subtle section-gap">已保存的译文会跳过，不重复提交。超时或连接中断后，原服务仍可能计费；切换重试无法保证免除这部分费用。原文仅发给你选定的服务。</p>
    <div class="form-actions"><button class="btn primary" type="submit">保存分担设置</button></div></form>`;
  const form = document.getElementById('script-pool-form');
  form.addEventListener('input', () => {
    form.dataset.dirty = 'true';
    state.dirty = true;
    updateScriptPoolDisabled();
  });
  form.addEventListener('change', () => {
    form.dataset.dirty = 'true';
    state.dirty = true;
    updateScriptPoolDisabled();
  });
  form.onsubmit = async (event) => {
    event.preventDefault();
    await busy(form.querySelector('[type=submit]'), async () => {
      const providers = [...form.querySelectorAll('[data-script-provider]')]
        .filter((row) => row.querySelector('[data-pool-select]').checked)
        .map((row) => ({
          providerId: row.dataset.scriptProvider,
          concurrency: Number(row.querySelector('[data-pool-concurrency]').value),
        }));
      const pool = await api('/api/script-translation/settings', {
        method: 'PUT',
        body: {
          multiProvider: form.elements.multiProvider.checked,
          batchLines: Number(form.elements.batchLines.value),
          providers,
        },
      });
      scriptUi.pool = pool;
      state.settings.scriptTranslation = pool;
      form.dataset.dirty = 'false';
      state.dirty = false;
      toast('API 分担设置已保存');
      if (scriptUi.status) await drawScriptWorkspace(scriptUi.status);
    });
    updateScriptPoolDisabled();
  };
  updateScriptPoolDisabled();
}

function updateScriptPoolDisabled() {
  const form = document.getElementById('script-pool-form');
  if (!form) return;
  const busy = scriptUi.jobs.some((j) => j.busy),
    enabled = form.elements.multiProvider.checked;
  form.elements.multiProvider.disabled = busy;
  form.elements.batchLines.disabled = busy;
  for (const row of form.querySelectorAll('[data-script-provider]')) {
    row.querySelector('[data-pool-select]').disabled = busy || !enabled;
    row.querySelector('[data-pool-concurrency]').disabled =
      busy || !enabled || !row.querySelector('[data-pool-select]').checked;
  }
  form.querySelector('[type=submit]').disabled = busy;
}
function checkScriptPoolSaved() {
  if (document.getElementById('script-pool-form')?.dataset.dirty === 'true')
    throw new Error('请先保存 API 分担设置，再开始翻译。');
}

async function drawScriptWorkspace(job) {
  const holder = document.getElementById('script-workspace');
  if (!holder) return;
  scriptUi.status = job;
  if (job.external?.enabled && scriptUi.method !== 'mcp') {
    scriptUi.method = 'mcp';
    drawScriptExternalPanel();
  }
  const locked = job.busy || job.external?.enabled;
  const game = gameById(job.gameId),
    provider = providerById(game?.providerId || state.settings.providerId);
  const progress = job.total ? Math.min(100, Math.max(0, Math.round((job.translated / job.total) * 100))) : 0;
  holder.innerHTML =
    `<div class="card-header"><div><h2>${icon('translate')}对白预览与翻译</h2><p>${esc(job.gameName)} · ${esc(job.archive)}</p>` +
    `</div>${badge(scriptStage(job.stage), job.busy ? 'blue' : '')}</div>
    <p class="card-description" role="status" id="script-progress-message">${esc(job.message)}</p>
    <div id="script-provider-progress"></div>
    <div id="script-external-state"></div>
    ${
      job.total
        ? `<div class="script-progress"><div><strong>翻译进度</strong><span>${number(job.translated)} / ${number(job.total)} 条 · ${progress}%</span>` +
          `</div><progress max="100" value="${progress}" aria-label="翻译进度">` +
          `${progress}%</progress>` +
          `</div>`
        : ''
    }
    <div class="script-summary section-gap"><span>识别剧本 <b>${number(job.scriptFiles)}</b> 份</span><span>对白 <b>${number(job.total)}</b>` +
    ` 条</span><span>已译 <b>${number(job.translated)}</b>` +
    ` 条</span><span>待译 <b>${number(job.pending)}</b> 条</span><span>排队或处理中 <b>${number(job.queued)}</b> 条</span>` +
    `<span>失败 <b>${number(job.failedLines)}</b> 条</span><span>仅标点 <b>${number(job.skipped)}</b> 条</span></div>
    ${
      locked
        ? `<div class="actions section-gap"><button class="btn" data-script="cancel" data-id="${esc(job.id)}">` +
          `${icon('pause')}${job.external?.enabled ? '暂停外部领取' : job.stage === 'translating' ? '暂停翻译' : '取消提取'}</button>` +
          `</div>`
        : ''
    }
    ${job.warnings?.length ? notice(job.warnings.map((w) => esc(w)).join('<br>'), 'warning') : ''}
    <div class="actions section-gap"><button class="btn primary small" data-script="patch" data-id="${esc(job.id)}" ${locked || !job.translated || job.patchSupported === false ? 'disabled' : ''} title="${job.patchSupported === false ? '此引擎的汉化补丁尚未适配，可导出已译 JSON / TXT' : ''}">` +
    `${icon('download')}生成汉化补丁</button>` +
    `<button class="btn small" data-script="review" data-id="${esc(job.id)}" ${job.total ? '' : 'disabled'}>${icon('check')}检查译文</button>` +
    `<button class="btn small" data-script="identical" data-id="${esc(job.id)}" ${job.translated ? '' : 'disabled'}>查找原译文相同</button>` +
    `<button class="btn small" data-script="folder" data-id="${esc(job.id)}">` +
    `${icon('folder')}打开任务文件夹</button>` +
    `<button class="btn small" data-script="export-json" data-id="${esc(job.id)}" ${job.translated ? '' : 'disabled'}>` +
    `${icon('download')}导出已译 JSON</button>` +
    `<button class="btn small" data-script="export-txt" data-id="${esc(job.id)}" ${job.translated ? '' : 'disabled'}>` +
    `${icon('download')}导出已译 TXT</button>` +
    `<button class="btn small" data-script="scope-resume" ${locked || !job.resumePending ? 'disabled' : ''}>` +
    `${icon('play')}继续上次范围${job.resumePending ? `（${number(job.resumePending)} 条）` : ''}</button>` +
    `</div>
    <div id="script-patch-output"></div>
    ${
      job.total
        ? `<div class="field-row section-gap"><label class="field">剧本<select id="script-filter-file"><option value="">` +
          `所有剧本（仅筛选，不会全选）</option>${job.files
            .map(
              (f) =>
                `<option value="${esc(f.name)}" ${f.name === scriptUi.file ? 'selected' : ''}>` +
                `${esc(f.name)} · ${number(f.count)} 条</option>`,
            )
            .join('')}</select>` +
          `</label><label class="field">场景<select id="script-filter-scene"></select></label></div>
    <div class="toolbar"><div class="search-field"><input id="script-search" type="search" maxlength="120" placeholder="搜索原文、译文或角色名" value="${esc(scriptUi.query)}">` +
          `</div><button class="btn small ${scriptUi.failedOnly ? 'primary' : ''}" type="button" data-script="filter-failed" aria-pressed="${scriptUi.failedOnly}">只看失败</button></div>
    <div class="script-selection"><div><strong id="script-selected-count">已选择 ${number(scriptUi.selected.size)} 条</strong>` +
          `<small>${scriptUi.method === 'mcp' ? '由已连接的外部助手领取与翻译' : scriptUi.pool?.multiProvider ? `使用已勾选的 ${number(scriptUi.pool.providers.length)} 个 API 分担` : provider ? `使用 ${esc(provider.name)} · ${esc(provider.model || '未填写模型')}` : '请先配置游戏使用的 AI 服务'} · 最多 1000 条；筛选不会清除其他已选项</small>` +
          `</div><div class="actions"><button class="btn small" data-script="select-page" ${locked ? 'disabled' : ''}>` +
          `${scriptUi.failedOnly ? '选择本页失败项' : '仅选本页待译'}</button><button class="btn small" data-script="select-failed" ${scriptUi.failedOnly ? '' : 'hidden'} ${locked ? 'disabled' : ''}>选择筛选出的失败项</button><button class="btn small" data-script="clear" ${locked ? 'disabled' : ''}>` +
          `清空选择</button><button class="btn primary" id="script-start-selected" data-script="translate" data-id="${esc(job.id)}" ${locked || !scriptUi.selected.size ? 'disabled' : ''}>` +
          `${icon('play')}${scriptUi.method === 'mcp' ? '准备选中对白' : '翻译选中对白'}</button>` +
          `<button class="btn" data-script="scope-file" ${locked || !scriptUi.file ? 'disabled' : ''}>` +
          `${scriptUi.method === 'mcp' ? '准备当前剧本' : '翻译当前剧本'}</button>` +
          `<button class="btn" data-script="scope-all" ${locked || job.total <= job.translated + job.skipped ? 'disabled' : ''}>` +
          `${scriptUi.method === 'mcp' ? '准备全部待译' : '翻译全部待译'}</button>` +
          `</div></div>
    <div id="script-segment-table" class="script-table-wrap"></div><div class="actions section-gap" id="script-pagination">` +
          `</div>`
        : ''
    }`;
  drawScriptProviderProgress(job);
  await drawScriptPatchOutputs(job.id);
  if (!job.total) return;
  document.getElementById('script-filter-file').onchange = async (e) => {
    scriptUi.file = e.target.value;
    scriptUi.scene = '';
    scriptUi.page = 1;
    await loadScriptPage();
  };
  document.getElementById('script-filter-scene').onchange = async (e) => {
    scriptUi.scene = e.target.value;
    scriptUi.page = 1;
    await loadScriptPage();
  };
  document.getElementById('script-search').oninput = debounce(async (e) => {
    scriptUi.query = e.target.value;
    scriptUi.page = 1;
    await loadScriptPage();
  }, 250);
  await loadScriptPage();
}

function drawScriptProviderProgress(job) {
  drawScriptExternalState(job);
  const holder = document.getElementById('script-provider-progress');
  if (!holder) return;
  holder.innerHTML = job.providers?.length
    ? `<div class="script-provider-progress section-gap">${job.providers
        .map(
          (p) =>
            `<div><strong>${esc(p.name)}</strong>` +
            `<small>${esc(p.model)} · 并发 ${number(p.working)} / ${number(p.concurrency)}</small>` +
            `<small>已完成 ${number(p.completedLines)} 条 · 已尝试 ${number(p.requests)} 批 · 失败 ${number(p.failures)} 批</small>` +
            `<small>${esc(p.status)}</small>` +
            `</div>`,
        )
        .join('')}</div>`
    : '';
}

async function loadScriptPage() {
  const sequence = ++scriptPageSequence,
    id = scriptUi.job;
  const query = new URLSearchParams({
    file: scriptUi.file,
    scene: scriptUi.scene,
    q: scriptUi.query,
    failedOnly: String(scriptUi.failedOnly),
    page: String(scriptUi.page),
  });
  const page = await api(`/api/script-translation/jobs/${id}/segments?${query}`);
  if (sequence !== scriptPageSequence || state.route !== 'scripts' || scriptUi.job !== id) return;
  scriptUi.rows = page;
  scriptUi.page = page.page;
  const scene = document.getElementById('script-filter-scene');
  if (scene)
    scene.innerHTML =
      '<option value="">所有场景</option>' +
      page.scenes
        .map((s) => `<option value="${esc(s)}" ${s === scriptUi.scene ? 'selected' : ''}>${esc(s)}</option>`)
        .join('');
  const table = document.getElementById('script-segment-table');
  if (!table) return;
  table.innerHTML = page.items.length
    ? `<table class="table script-table"><thead><tr><th>选择</th><th>位置与角色</th><th>原文</th><th>译文</th><th>状态</th>` +
      `</tr></thead><tbody>${page.items
        .map(
          (s) =>
            `<tr>` +
            `<td><input type="checkbox" data-script-line="${esc(s.id)}" aria-label="选择第 ${number(s.index)} 条对白" ${scriptUi.selected.has(s.id) ? 'checked' : ''} ${scriptUi.status?.busy || scriptUi.status?.external?.enabled || ['translated', 'skipped', 'queued'].includes(s.status) ? 'disabled' : ''}>` +
            `</td><td><small>${esc(s.file)}<br>` +
            `${esc(s.scene)} · ${number(s.index)}</small>` +
            `<p>${esc(s.speaker || '旁白')}</p>` +
            `</td><td><div class="script-text">${esc(s.source)}</div>` +
            `</td><td><div class="script-text">${esc(s.translation || '—')}</div>` +
            `${s.error ? `<small class="subtle">` + `${esc(s.error)}</small>` : ''}</td>` +
            `<td>${badge(scriptLineStage(s.status), s.status === 'translated' ? 'green' : s.status === 'failed' ? 'red' : '')}</td>` +
            `</tr>`,
        )
        .join('')}</tbody>` +
      `</table>`
    : '<p class="card-description">没有匹配的对白。</p>';
  document.getElementById('script-pagination').innerHTML =
    `<button class="btn small" data-script="previous-page" ${page.page <= 1 ? 'disabled' : ''}>上一页</button><span class="tiny subtle">` +
    `第 ${number(page.page)} / ${number(page.pages)} 页 · 筛选出 ${number(page.total)} 条</span>` +
    `<button class="btn small" data-script="next-page" ${page.page >= page.pages ? 'disabled' : ''}>` +
    `下一页</button>`;
  drawScriptSelection();
}

function drawScriptSelection() {
  const count = document.getElementById('script-selected-count');
  if (count) count.textContent = `已选择 ${number(scriptUi.selected.size)} 条`;
  const start = document.getElementById('script-start-selected');
  if (start)
    start.disabled = !!(scriptUi.status?.busy || scriptUi.status?.external?.enabled) || scriptUi.selected.size === 0;
  const file = document.querySelector('[data-script=scope-file]');
  if (file) file.disabled = !!(scriptUi.status?.busy || scriptUi.status?.external?.enabled) || !scriptUi.file;
  document
    .querySelectorAll('[data-script-line]')
    .forEach((input) => (input.checked = scriptUi.selected.has(input.dataset.scriptLine)));
}

async function previewScriptScope(scope) {
  if (scriptUi.method === 'mcp') return previewExternalScript(scope);
  checkScriptPoolSaved();
  const id = scriptUi.job;
  const request = { scope, file: scope === 'file' ? scriptUi.file : null };
  const plan = await api(`/api/script-translation/jobs/${id}/scope/preview`, { method: 'POST', body: request });
  modal(
    '确认翻译范围',
    '确认下面的范围和条数后，才会调用你配置的 AI 服务。',
    `
    <form id="script-scope-form"><p><strong>${esc(plan.label)}</strong></p>
    <div class="script-summary section-gap"><span>本次待译 <b>${number(plan.lines)}</b> 条</span><span>去重后 <b>${number(plan.uniqueLines)}</b>` +
      ` 条</span><span>原文 <b>${number(plan.characters)}</b>` +
      ` 字符</span></div>
    <div class="script-provider-progress section-gap">${(plan.providers || [])
      .map(
        (p) =>
          `<div><strong>${esc(p.name)}</strong>` +
          `<small>${esc(p.model)} · 并发 ${number(p.concurrency)}</small>` +
          `</div>`,
      )
      .join('')}</div>
    <p class="card-description section-gap">已完成 ${number(plan.completed)} 条、仅标点 ${number(plan.skipped)} 条将跳过。共用当前提示词和术语表；超时后切换重试仍可能计费。</p>
    <p class="card-description section-gap">预计分 ${number(plan.batches)} 批处理，实际请求次数受缓存和重试影响。费用由你配置的服务计收；开始后可随时暂停，已完成译文保留。</p>
    <div class="form-actions"><button class="btn" type="button" data-action="close-modal">先不翻译</button><button class="btn primary" type="submit">` +
      `开始翻译 ${number(plan.lines)} 条</button>` +
      `</div></form>`,
  );
  const form = document.getElementById('script-scope-form');
  form.onsubmit = async (event) => {
    event.preventDefault();
    await busy(form.querySelector('[type=submit]'), async () => {
      const job = await api(`/api/script-translation/jobs/${id}/scope/translate`, {
        method: 'POST',
        body: { ...request, fingerprint: plan.fingerprint, confirmed: true },
      });
      closeModal(true);
      scriptUi.selected.clear();
      scriptUi.jobs = scriptUi.jobs.map((j) => (j.id === id ? job : j));
      drawScriptJobs();
      await drawScriptWorkspace(job);
      scheduleScriptPoll();
    });
  };
}
function scheduleScriptPoll() {
  clearTimeout(scriptPoll);
  if (state.route === 'scripts' && scriptUi.jobs.some((j) => j.busy || j.external?.enabled))
    scriptPoll = setTimeout(pollScripts, 900);
}
async function pollScripts() {
  if (state.route !== 'scripts') return;
  try {
    const jobs = await api('/api/script-translation/jobs');
    if (state.route !== 'scripts') return;
    const before = scriptUi.status;
    scriptUi.jobs = jobs;
    drawScriptJobs();
    const current = jobs.find((j) => j.id === scriptUi.job);
    if (current) {
      if (
        !before ||
        current.stage !== before.stage ||
        current.finished !== before.finished ||
        current.failed !== before.failed ||
        current.busy !== before.busy
      )
        await drawScriptWorkspace(current);
      else {
        scriptUi.status = current;
        const message = document.getElementById('script-progress-message');
        if (message) message.textContent = current.message;
        drawScriptProviderProgress(current);
      }
    }
  } catch (error) {
    toast(error.message, 'error');
  }
  scheduleScriptPoll();
}

document.addEventListener('change', (event) => {
  const input = event.target.closest('[data-script-line]');
  if (!input) return;
  if (input.checked) {
    if (scriptUi.selected.size >= 1000) {
      input.checked = false;
      toast('每次最多选择 1000 条对白，可分批继续', 'info');
      return;
    }
    scriptUi.selected.add(input.dataset.scriptLine);
  } else scriptUi.selected.delete(input.dataset.scriptLine);
  drawScriptSelection();
});
document.addEventListener('click', async (event) => {
  const button = event.target.closest('[data-script]');
  if (!button || button.disabled) return;
  await busy(button, async () => {
    const action = button.dataset.script,
      id = button.dataset.id || scriptUi.job;
    if (action === 'job') {
      if (scriptUi.job !== id) {
        scriptUi.job = id;
        scriptUi.selected.clear();
        scriptUi.file = '';
        scriptUi.scene = '';
        scriptUi.query = '';
        scriptUi.failedOnly = false;
        scriptUi.page = 1;
      }
      drawScriptJobs();
      await drawScriptWorkspace(await api(`/api/script-translation/jobs/${id}`));
    } else if (action === 'refresh') {
      scriptUi.jobs = await api('/api/script-translation/jobs');
      drawScriptJobs();
      if (scriptUi.job) await drawScriptWorkspace(await api(`/api/script-translation/jobs/${scriptUi.job}`));
      scheduleScriptPoll();
    } else if (action === 'filter-failed') {
      scriptUi.failedOnly = !scriptUi.failedOnly;
      scriptUi.page = 1;
      button.setAttribute('aria-pressed', String(scriptUi.failedOnly));
      button.classList.toggle('primary', scriptUi.failedOnly);
      const selectPage = document.querySelector('[data-script="select-page"]');
      if (selectPage) selectPage.textContent = scriptUi.failedOnly ? '选择本页失败项' : '仅选本页待译';
      const selectFailed = document.querySelector('[data-script="select-failed"]');
      if (selectFailed) selectFailed.hidden = !scriptUi.failedOnly;
      await loadScriptPage();
    } else if (action === 'select-failed') {
      const query = new URLSearchParams({ file: scriptUi.file, scene: scriptUi.scene, q: scriptUi.query });
      const selected = await api(`/api/script-translation/jobs/${id}/failed-selection?${query}`);
      if (
        scriptUi.job !== id ||
        !scriptUi.failedOnly ||
        query.toString() !==
          new URLSearchParams({ file: scriptUi.file, scene: scriptUi.scene, q: scriptUi.query }).toString()
      )
        return;
      let added = 0;
      for (const lineId of selected.ids) {
        if (scriptUi.selected.has(lineId)) continue;
        if (scriptUi.selected.size >= 1000) break;
        scriptUi.selected.add(lineId);
        added++;
      }
      drawScriptSelection();
      toast(`筛选出 ${number(selected.total)} 条失败项，新增选择 ${number(added)} 条；每次最多选择 1000 条。`, 'info');
    } else if (action === 'select-page') {
      for (const row of scriptUi.rows?.items || [])
        if (['pending', 'failed'].includes(row.status) && scriptUi.selected.size < 1000) scriptUi.selected.add(row.id);
      drawScriptSelection();
    } else if (action === 'clear') {
      scriptUi.selected.clear();
      drawScriptSelection();
    } else if (action === 'previous-page' || action === 'next-page') {
      scriptUi.page += action === 'next-page' ? 1 : -1;
      await loadScriptPage();
    } else if (action === 'patch') await previewScriptPatch(id);
    else if (action === 'review') showScriptReview(id, await api(`/api/script-translation/jobs/${id}/review`));
    else if (action === 'identical') await showIdenticalTranslations(id);
    else if (action === 'patch-folder')
      await api(`/api/script-translation/jobs/${id}/patch/${button.dataset.patch}/open-folder`, { method: 'POST' });
    else if (action === 'patch-delete') confirmDeleteScriptPatch(id, button.dataset.patch, button.dataset.created);
    else if (action === 'folder') await api(`/api/script-translation/jobs/${id}/open-folder`, { method: 'POST' });
    else if (['scope-file', 'scope-all', 'scope-resume'].includes(action))
      await previewScriptScope(action === 'scope-file' ? 'file' : action === 'scope-resume' ? 'resume' : 'all');
    else if (action === 'export-json' || action === 'export-txt')
      await api(`/api/script-translation/jobs/${id}/export/${action === 'export-json' ? 'json' : 'txt'}`, {
        download: true,
      });
    else if (action === 'cancel') {
      await api(`/api/script-translation/jobs/${id}/cancel`, { method: 'POST' });
      scheduleScriptPoll();
    } else if (action === 'translate') {
      if (scriptUi.method === 'mcp') {
        await previewExternalScript('selected');
        return;
      }
      checkScriptPoolSaved();
      const ids = [...scriptUi.selected];
      if (!ids.length || ids.length > 1000) return;
      const job = await api(`/api/script-translation/jobs/${id}/translate`, {
        method: 'POST',
        body: { ids, confirmed: true },
      });
      scriptUi.selected.clear();
      scriptUi.jobs = scriptUi.jobs.map((j) => (j.id === job.id ? job : j));
      drawScriptJobs();
      await drawScriptWorkspace(job);
      scheduleScriptPoll();
    }
  });
});

async function drawScriptPatchOutputs(id) {
  const outputs = await api(`/api/script-translation/jobs/${id}/patches`);
  if (scriptUi.job !== id) return;
  const holder = document.getElementById('script-patch-output');
  if (!holder) return;
  holder.innerHTML = outputs.length
    ? `<div class="section-gap"><strong>已生成的汉化补丁</strong>${outputs
        .slice(0, 3)
        .map(
          (p) =>
            `<div class="script-patch-result"><div><small>${esc(dateText(p.createdAt))} · 已译 ${number(p.translated)} 条 · ${number(p.files)} 份资源</small>` +
            `<code>${esc(p.directory)}</code>` +
            `<small>把整个 RikaPatch 文件夹放入游戏目录，再运行其中的 RikaPatchLauncher.exe。</small></div><span class="actions"><button class="btn small" data-script="patch-folder" data-id="${esc(id)}" data-patch="${esc(p.id)}">` +
            `${icon('folder')}打开补丁文件夹</button>` +
            `<button class="btn small" data-script="patch-delete" data-id="${esc(id)}" data-patch="${esc(p.id)}" data-created="${esc(dateText(p.createdAt))}">` +
            `${icon('trash')}删除</button>` +
            `</span></div>`,
        )
        .join('')}</div>`
    : '';
}
function confirmDeleteScriptPatch(id, patchId, created) {
  modal(
    '删除汉化补丁',
    '确定删除这个已生成的补丁吗？',
    `<p>生成时间：${esc(created)}</p><p class="card-description section-gap">将永久删除梨花中保存的这份补丁文件。剧本译文、游戏文件和已复制到其他位置的补丁不受影响。</p>` +
      `<div class="form-actions"><button class="btn" type="button" data-action="close-modal">取消</button><button class="btn primary" type="button" id="confirm-script-patch-delete">` +
      `确认删除</button></div>`,
  );
  document.getElementById('confirm-script-patch-delete').onclick = async (event) => {
    await busy(event.currentTarget, async () => {
      await api(`/api/script-translation/jobs/${id}/patch/${patchId}`, { method: 'DELETE' });
      closeModal(true);
      await drawScriptPatchOutputs(id);
      toast('补丁已删除');
    });
  };
}
async function showIdenticalTranslations(id, page = 1) {
  const report = await api(`/api/script-translation/jobs/${id}/identical-translations?page=${page}`);
  if (scriptUi.job === id) showScriptReview(id, report, '', true);
}
function showScriptReview(id, report, errorMessage = '', identical = false) {
  const labels = { format: '控制符差异', failed: '翻译失败', long: '长句提醒', identical: '原译文相同' };
  const preview = (value) => {
    const text = String(value || '');
    return esc(text.length > 600 ? `${text.slice(0, 600)}…` : text);
  };
  modal(
    identical ? '查找原译文相同' : '检查译文',
    '本地检查不会修改译文，也不会调用 AI。',
    (identical
      ? `<div class="script-summary"><span>原文与译文完全相同 <b>${number(report.total)}</b> 条</span></div>`
      : `<div class="script-summary"><span>待译 <b>${number(report.pending)}</b> 条</span><span>排队或处理中 <b>${number(report.queued)}</b> 条</span>` +
        `<span>翻译失败 <b>${number(report.failed)}</b> 条</span><span>控制符差异 <b>${number(report.invalid)}</b> 条</span>` +
        `<span>长句提醒 <b>${number(report.longLines)}</b> 条</span></div>`) +
      `${errorMessage ? `<div class="section-gap">${notice(esc(errorMessage), 'warning')}</div>` : ''}` +
      (identical
        ? `<p class="card-description section-gap">仅比较已完成的译文，完全一致才会列出。人名、拟声词等可能需要保留原文，请先核对。</p>`
        : `<p class="card-description section-gap">${esc(report.message)} 最多展示 ${number(report.issueLimit)} 条问题。</p>`) +
      `<div class="script-review-list section-gap">${
        report.issues.length
          ? report.issues
              .map(
                (item, index) =>
                  `<article class="script-review-item"><div class="script-review-heading"><strong>${esc(labels[item.kind] || '检查提示')}</strong>` +
                  `<span class="actions"><button class="btn small" type="button" data-review-index="${index}">定位对白</button>` +
                  `${item.kind !== 'failed' ? `<button class="btn small" type="button" data-review-edit="${index}" ${scriptUi.status?.busy || scriptUi.status?.external?.enabled ? 'disabled' : ''}>修订译文</button>` : ''}</span></div>` +
                  `<p class="tiny subtle">${esc(item.file)} · ${esc(item.scene)} · 第 ${number(item.index)} 条${item.speaker ? ` · ${esc(item.speaker)}` : ''}</p>` +
                  `<p>${esc(item.message)}</p><p class="tiny subtle">${esc(item.suggestion)}</p>` +
                  `<div class="script-review-text"><small>原文</small><div>${preview(item.source)}</div></div>` +
                  `<div class="script-review-text"><small>译文</small><div>${preview(item.translation) || '（尚无译文）'}</div></div></article>`,
              )
              .join('')
          : '<p class="card-description">没有发现需要提示的问题。</p>'
      }</div>${identical ? `<div class="actions section-gap"><button class="btn small" type="button" data-identical-page="${report.page - 1}" ${report.page <= 1 ? 'disabled' : ''}>上一页</button><span class="tiny subtle">第 ${number(report.page)} / ${number(report.pages)} 页</span><button class="btn small" type="button" data-identical-page="${report.page + 1}" ${report.page >= report.pages ? 'disabled' : ''}>下一页</button></div>` : ''}<div class="form-actions"><button class="btn" type="button" data-action="close-modal">关闭</button></div>`,
    { wide: true },
  );
  document.querySelectorAll('[data-review-index]').forEach((button) => {
    button.onclick = () =>
      busy(button, async () => {
        const item = report.issues[Number(button.dataset.reviewIndex)];
        const location = await api(
          `/api/script-translation/jobs/${id}/segments/${encodeURIComponent(item.id)}/location`,
        );
        if (scriptUi.job !== id) return;
        scriptUi.file = location.file;
        scriptUi.scene = location.scene;
        scriptUi.query = '';
        scriptUi.failedOnly = false;
        scriptUi.page = location.page;
        closeModal(true);
        const file = document.getElementById('script-filter-file');
        if (file) file.value = location.file;
        const search = document.getElementById('script-search');
        if (search) search.value = '';
        const filter = document.querySelector('[data-script="filter-failed"]');
        if (filter) {
          filter.setAttribute('aria-pressed', 'false');
          filter.classList.remove('primary');
        }
        const selectFailed = document.querySelector('[data-script="select-failed"]');
        if (selectFailed) selectFailed.hidden = true;
        const selectPage = document.querySelector('[data-script="select-page"]');
        if (selectPage) selectPage.textContent = '仅选本页待译';
        await loadScriptPage();
        const input = [...document.querySelectorAll('[data-script-line]')].find(
          (el) => el.dataset.scriptLine === item.id,
        );
        const row = input?.closest('tr');
        if (row) {
          row.classList.add('script-review-focus');
          row.scrollIntoView({ block: 'center' });
        }
      });
  });
  document.querySelectorAll('[data-review-edit]').forEach((button) => {
    button.onclick = () => editScriptReviewLine(id, report.issues[Number(button.dataset.reviewEdit)]);
  });
  document.querySelectorAll('[data-identical-page]').forEach((button) => {
    button.onclick = () => busy(button, () => showIdenticalTranslations(id, Number(button.dataset.identicalPage)));
  });
}
function editScriptReviewLine(id, item) {
  modal(
    '修订剧本译文',
    '只修改这条任务译文；已生成的补丁需重新生成。',
    `<form id="script-review-edit-form"><p class="tiny subtle">${esc(item.file)} · ${esc(item.scene)} · 第 ${number(item.index)} 条</p>` +
      `<div class="script-review-text"><small>原文</small><div>${esc(item.source)}</div></div>` +
      `<label class="field section-gap">译文<textarea name="translation" rows="7" maxlength="32768" required>${esc(item.translation)}</textarea></label>` +
      `<p class="tiny subtle">保存时会检查变量和控制符，不调用 AI，也不会修改游戏文件。</p>` +
      `<div class="form-actions"><button class="btn" type="button" data-action="close-modal">取消</button><button class="btn primary" type="submit">确认保存</button></div></form>`,
    { wide: true },
  );
  const form = document.getElementById('script-review-edit-form');
  form.onsubmit = (event) => {
    event.preventDefault();
    busy(form.querySelector('[type=submit]'), async () => {
      await api(`/api/script-translation/jobs/${id}/segments/${encodeURIComponent(item.id)}/translation`, {
        method: 'PUT',
        body: { translation: form.elements.translation.value, expected: item.translation, confirmed: true },
      });
      closeModal(true);
      if (scriptUi.job === id) await drawScriptWorkspace(await api(`/api/script-translation/jobs/${id}`));
      toast('任务译文已保存；已有补丁需重新生成');
    });
  };
}
async function previewScriptPatch(id) {
  const report = await api(`/api/script-translation/jobs/${id}/review`);
  let plan, catalog;
  try {
    [plan, catalog] = await Promise.all([
      api(`/api/script-translation/jobs/${id}/patch/preview`, { method: 'POST', body: { chineseFont: true } }),
      api('/api/fonts'),
    ]);
  } catch (error) {
    if (report.invalid) return showScriptReview(id, report, error.message);
    throw error;
  }
  const fonts = catalog.filter((f) => f.family.length <= 31);
  modal(
    '生成汉化补丁',
    '使用此任务所有已完成译文；当前的勾选和筛选不影响补丁范围。',
    `
    <form id="script-patch-form"><p><strong>${esc(plan.gameName)}</strong></p>
    <div class="script-summary section-gap"><span>回填已译 <b>${number(plan.translated)}</b> 条</span><span>涉及资源 <b>` +
      `${number(plan.files)}</b>` +
      ` 份</span><span>保留原文 <b>${number(plan.remaining)}</b>` +
      ` 条</span></div>
    <p class="card-description section-gap">检查结果：控制符差异 ${number(report.invalid)} 条 · 长句提醒 ${number(report.longLines)} 条 · 翻译失败 ${number(report.failed)} 条。长句按字符数估算，游戏内效果需要确认。</p>
    <button class="btn small" type="button" data-patch-review>查看检查详情</button>
    <p class="card-description section-gap">生成独立补丁文件夹，不调用 AI。复制到游戏目录后，通过补丁启动器加载；原来的资源包和存档不改动。</p>
    <label class="script-pool-toggle section-gap"><input type="checkbox" name="chineseFont" checked>启用中文字体替换（避免中文缺字）</label>
    <label class="field section-gap">补丁字体<select name="fontFamily">${fonts
      .map(
        (f) =>
          `<option value="${esc(f.family)}" ${f.family === plan.fontFamily ? 'selected' : ''}>` +
          `${esc(f.displayName || f.family)}</option>`,
      )
      .join('')}</select>` +
      `</label>
    <div class="field"><label>字体文件（可选）</label><div class="inline-input"><input name="fontFileLabel" placeholder="也可以选择本机 TTF / OTF 字体文件" readonly>` +
      `<button class="btn" type="button" data-patch-pick-font>选择字体文件</button></div><small id="script-patch-font-hint">` +
      `系统字体需在运行游戏的电脑上安装；选择文件后会随补丁携带，只在游戏进程内加载。</small><button class="btn small ghost" type="button" data-patch-clear-font hidden>` +
      `改用系统字体</button></div>
    <p class="tiny subtle">请选择包含中文的字体。分享带字体文件的补丁前，请确认该字体允许再分发。</p>
    <p class="tiny subtle section-gap">支持已适配的 KiriKiri、WillPlus、YU-RIS、ExHIBIT、Escu:de、Siglus 和 BGI 剧本资源。不修改角色名、菜单图片或剧情指令。补丁只加载原始内容匹配的剧本，实际排版仍需在游戏中检查。</p>
    <div class="form-actions"><button class="btn" type="button" data-action="close-modal">取消</button><button class="btn primary" type="submit">` +
      `生成补丁</button></div></form>`,
  );
  const form = document.getElementById('script-patch-form');
  form.querySelector('[data-patch-review]').onclick = () => showScriptReview(id, report);
  let fontFile = null;
  function updatePatchFontControls() {
    const enabled = form.elements.chineseFont.checked;
    form.elements.fontFamily.disabled = !enabled || !!fontFile;
    form.querySelector('[data-patch-pick-font]').disabled = !enabled;
  }
  form.elements.chineseFont.onchange = updatePatchFontControls;
  form.querySelector('[data-patch-clear-font]').onclick = () => {
    fontFile = null;
    form.elements.fontFileLabel.value = '';
    form.elements.fontFamily.value = plan.fontFamily;
    form.querySelector('[data-patch-clear-font]').hidden = true;
    document.getElementById('script-patch-font-hint').textContent =
      '使用选中的系统字体；运行游戏的电脑也需要安装该字体。';
    updatePatchFontControls();
  };
  form.querySelector('[data-patch-pick-font]').onclick = async () => {
    const start = form.querySelector('[type=submit]');
    start.disabled = true;
    try {
      await busy(form.querySelector('[data-patch-pick-font]'), async () => {
        const selected = await api('/api/font-maker/pick', { method: 'POST' });
        if (!selected.path) return;
        const font = await api(`/api/script-translation/jobs/${id}/patch/font`, {
          method: 'POST',
          body: { sourcePath: selected.path },
        });
        if (!form.isConnected) return;
        fontFile = font.id;
        form.elements.fontFileLabel.value = font.fileName + ' · ' + font.family;
        if (![...form.elements.fontFamily.options].some((o) => o.value === font.family))
          form.elements.fontFamily.add(new Option(font.family, font.family));
        form.elements.fontFamily.value = font.family;
        document.getElementById('script-patch-font-hint').textContent =
          '已保存字体副本，生成时随补丁携带，不会修改或安装原字体。';
        form.querySelector('[data-patch-clear-font]').hidden = false;
      });
    } finally {
      if (form.isConnected) {
        start.disabled = false;
        updatePatchFontControls();
      }
    }
  };
  updatePatchFontControls();
  form.onsubmit = async (event) => {
    event.preventDefault();
    await busy(form.querySelector('[type=submit]'), async () => {
      const chineseFont = form.elements.chineseFont.checked;
      const fontFamily = form.elements.fontFamily.value;
      const options = { chineseFont, fontFamily, fontFile };
      const current =
        chineseFont === plan.chineseFont && fontFamily === plan.fontFamily && fontFile === plan.fontFile
          ? plan
          : await api(`/api/script-translation/jobs/${id}/patch/preview`, { method: 'POST', body: options });
      await api(`/api/script-translation/jobs/${id}/patch/generate`, {
        method: 'POST',
        body: { ...options, fingerprint: current.fingerprint, confirmed: true },
      });
      closeModal(true);
      await drawScriptPatchOutputs(id);
      toast('汉化补丁已生成，打开文件夹查看使用说明');
    });
  };
}
