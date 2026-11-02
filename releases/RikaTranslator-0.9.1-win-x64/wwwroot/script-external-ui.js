'use strict';

function drawScriptExternalPanel() {
  const holder = document.getElementById('script-external-panel');
  if (!holder) return;
  holder.innerHTML =
    `<div class="card-header"><h2>${icon('plug')}翻译方式</h2><button class="btn small" data-script-external="config">` +
    `复制 MCP 配置</button></div>
    <label class="field">执行翻译的工具<select id="script-translation-method"><option value="api" ${scriptUi.method === 'api' ? 'selected' : ''}>` +
    `梨花调用 AI API</option><option value="mcp" ${scriptUi.method === 'mcp' ? 'selected' : ''}>` +
    `MCP 外部助手</option></select></label>
    <div id="script-external-help" class="section-gap"><p class="card-description">先在下方勾选对白，或选择当前剧本 / 全部待译，再确认准备范围。外部助手只能领取你确认的内容，不能自行扩大范围。</p>
    <form id="script-mcp-settings-form" class="section-gap"><label class="field">每批最多领取条数<input name="batchLines" type="number" min="1" max="50" step="1" required value="${number(scriptUi.mcp?.batchLines || 8)}">` +
    `<small>1–50 条，默认 8。较长对白或切换场景时，会分成更小的批次；单批原文最多 8,192 字符。只影响之后领取的批次，已经领取的批次可继续回传。</small></label><div class="form-actions">` +
    `<button class="btn" type="submit">保存批次设置</button></div></form>
    <p class="card-description section-gap">保持梨花打开。在支持本地 stdio MCP 的助手中添加复制的配置；不同助手的配置方式可能不同。刷新连接后，对它说：“用 rika_translator 领取梨花已准备的任务，按术语表翻译，逐批回传；完成或暂停后停止。”</p>
    <p class="tiny subtle section-gap">外部模式使用助手当前的模型，不调用梨花配置的 AI API。只提供已授权的文本、角色、相关术语、语言、提示词及游戏上下文，不提供服务密钥、鉴权头或游戏路径。每批 15 分钟内可续期；暂停会撤销未回传批次，已保存译文保留。</p>` +
    `</div>`;
  document.getElementById('script-translation-method').onchange = async (event) => {
    if (document.getElementById('script-pool-form')?.dataset.dirty === 'true') {
      event.target.value = scriptUi.method;
      toast('请先保存 API 分担设置，再切换翻译方式', 'info');
      return;
    }
    if (document.getElementById('script-mcp-settings-form')?.dataset.dirty === 'true') {
      event.target.value = scriptUi.method;
      toast('请先保存 MCP 批次设置，再切换翻译方式', 'info');
      return;
    }
    scriptUi.method = event.target.value;
    updateScriptExternalControls();
    if (scriptUi.status) await drawScriptWorkspace(scriptUi.status);
  };
  const form = document.getElementById('script-mcp-settings-form');
  form.addEventListener('input', () => {
    form.dataset.dirty = 'true';
    state.dirty = true;
  });
  form.onsubmit = async (event) => {
    event.preventDefault();
    await busy(form.querySelector('[type=submit]'), async () => {
      const batchLines = Number(form.elements.batchLines.value);
      const settings = await api('/api/script-translation/mcp/settings', { method: 'PUT', body: { batchLines } });
      scriptUi.mcp = settings;
      state.settings.mcpBatchLines = settings.batchLines;
      form.dataset.dirty = 'false';
      state.dirty = false;
      toast(`MCP 每批最多 ${number(settings.batchLines)} 条，之后领取的批次生效`);
    });
  };
  updateScriptExternalControls();
}
function updateScriptExternalControls() {
  const mode = document.getElementById('script-translation-method');
  if (!mode) return;
  const job = scriptUi.jobs.find((j) => j.id === scriptUi.job);
  mode.value = scriptUi.method;
  mode.disabled = !!(job?.busy || job?.external?.enabled);
  const external = scriptUi.method === 'mcp';
  const help = document.getElementById('script-external-help');
  if (help) help.hidden = !external;
  const pool = document.getElementById('script-pool-panel');
  if (pool) pool.hidden = external;
}
function drawScriptExternalState(job) {
  const holder = document.getElementById('script-external-state');
  if (!holder) return;
  const ext = job.external;
  holder.innerHTML = ext?.prepared
    ? `<div class="script-summary section-gap"><span>外部任务 <b>${ext.enabled ? '已开放领取' : job.stage === 'complete' ? '已完成' : '已暂停'}</b>` +
      `</span><span>本次范围 <b>${number(ext.approved)}</b>` +
      ` 条</span><span>已回传 <b>${number(ext.completed)}</b>` +
      ` 条</span><span>未完成 <b>${number(ext.pending)}</b> 条</span>` +
      `${ext.enabled ? `<span>等待领取 <b>${number(Math.max(0, ext.pending - ext.leased))}</b> 条</span><span>助手处理中 <b>${number(ext.leased)}</b> 条</span>` : ''}</div>`
    : '';
}
async function previewExternalScript(scope) {
  if (document.getElementById('script-mcp-settings-form')?.dataset.dirty === 'true')
    throw new Error('请先保存 MCP 批次设置，再准备翻译范围。');
  const id = scriptUi.job;
  const request = {
    scope,
    file: scope === 'file' ? scriptUi.file : null,
    ...(scope === 'selected' ? { ids: [...scriptUi.selected] } : {}),
  };
  const plan = await api(`/api/script-translation/jobs/${id}/external/preview`, { method: 'POST', body: request });
  modal(
    '准备给外部助手',
    '确认后允许已连接的 MCP 助手领取下面的范围。梨花不会在此步骤调用 AI。',
    `
    <form id="script-external-prepare-form"><p><strong>${esc(plan.label)}</strong></p>
    <div class="script-summary section-gap"><span>本次待译 <b>${number(plan.lines)}</b> 条</span><span>去重后 <b>${number(plan.uniqueLines)}</b>` +
      ` 条</span><span>原文 <b>${number(plan.characters)}</b>` +
      ` 字符</span></div>
    <p class="card-description section-gap">${esc(plan.from)} → ${esc(plan.to)}，按场景分批；共享当前提示词、相关术语和游戏上下文。已完成 ${number(plan.completed)} 条、仅标点 ${number(plan.skipped)} 条会跳过。</p>
    <p class="card-description section-gap">当前每批最多 ${number(plan.batchLines)} 条，预计 ${number(plan.batches)} 批；实际条数还受场景和总字数限制。</p>
    <p class="tiny subtle section-gap">由外部助手使用其当前模型翻译；费用及额度由助手服务决定。这里只开放这次确认的文本，不向助手提供梨花的 API Key。</p>
    <div class="form-actions"><button class="btn" type="button" data-action="close-modal">取消</button><button class="btn primary" type="submit">` +
      `准备 ${number(plan.lines)} 条</button>` +
      `</div></form>`,
  );
  const form = document.getElementById('script-external-prepare-form');
  form.onsubmit = async (event) => {
    event.preventDefault();
    await busy(form.querySelector('[type=submit]'), async () => {
      const job = await api(`/api/script-translation/jobs/${id}/external/prepare`, {
        method: 'POST',
        body: { ...request, fingerprint: plan.fingerprint, confirmed: true },
      });
      closeModal(true);
      scriptUi.selected.clear();
      scriptUi.jobs = scriptUi.jobs.map((j) => (j.id === id ? job : j));
      drawScriptJobs();
      await drawScriptWorkspace(job);
      scheduleScriptPoll();
      toast('已准备，等待外部助手领取');
    });
  };
}
document.addEventListener('click', async (event) => {
  const button = event.target.closest('[data-script-external=config]');
  if (!button) return;
  await busy(button, async () => {
    const text = JSON.stringify(await api('/api/script-translation/mcp/config'), null, 2);
    try {
      await navigator.clipboard.writeText(text);
      toast('MCP 配置已复制，请按助手的配置方式添加');
    } catch {
      modal(
        'MCP 配置',
        '请按助手的配置方式添加 rika_translator，并保留其他服务。',
        `<textarea rows="12" readonly>${esc(text)}</textarea>`,
      );
    }
  });
});
