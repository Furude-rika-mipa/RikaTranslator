/* GitHub update controls; installed changes still use the independent updater. */
let rikaUpdatePoll = null;
let rikaOnlineState = null;

function drawOnlineUpdates(s) {
  const panel = document.getElementById('online-update-panel');
  if (!panel) return;
  rikaOnlineState = s;
  const release = s.release;
  const labels = {idle:'从 GitHub 获取正式发布的新版本。',verifying:'正在校验已下载的更新…',checking:'正在检查 GitHub 新版本…',current:'当前已是最新版本。',available:'发现新版本，可以下载。',downloading:'正在下载更新…',preparing:'下载完成，正在校验和准备更新…',ready:'更新已下载并校验，可以直接安装。',cancelled:'更新下载已取消或删除，可以重新检查或下载。',error:'更新暂未完成，请重试。'};
  const canDownload = release && ['available','error','cancelled'].includes(s.stage);
  const downloading = ['downloading','preparing'].includes(s.stage);
  panel.innerHTML = `<p class="card-description" role="status">${esc(labels[s.stage]||labels.idle)}</p>
    ${release?`<div class="update-versions"><div><small>当前版本</small><strong>v${esc(s.currentVersion)}</strong></div><span class="update-arrow" aria-hidden="true">→</span><div><small>更新版本</small><strong>v${esc(release.version)}</strong></div></div><p class="tiny muted">Windows x64 · ${updateSize(release.size)}</p>`:''}
    ${downloading?`<progress class="online-update-progress" max="${release?.size||1}" value="${s.downloadedBytes||0}" aria-label="更新下载进度"></progress><p class="tiny muted">${updateSize(s.downloadedBytes||0)} / ${updateSize(release?.size||0)}</p>`:''}
    ${s.error?notice(esc(s.error),'error'):''}
    <div class="actions"><button class="btn" data-online-action="check" ${s.busy?'disabled':''}>${icon('refresh')}检查更新</button>
    ${canDownload?`<button class="btn primary" data-online-action="download" ${s.busy?'disabled':''}>${icon('download')}下载更新</button>`:''}
    ${s.stage==='ready'&&s.preview?`<button class="btn primary" data-online-action="install" ${s.busy?'disabled':''}>${icon('download')}安装并重启</button><button class="btn" data-online-action="discard" ${s.busy?'disabled':''}>${icon('trash')}删除已下载更新</button>`:''}
    ${downloading?'<button class="btn" data-online-action="cancel">取消下载</button>':''}
    <a class="btn" href="https://github.com/Furude-rika-mipa/RikaTranslator/releases" target="_blank" rel="noopener noreferrer">查看发布页</a></div>
    ${release?`<details class="advanced" ${s.stage==='available'?'open':''}><summary>更新说明</summary><div class="update-notes">${esc(release.notes)}</div></details>`:''}
    <p class="tiny muted section-gap">下载完成后关闭软件会保留更新包，重开可继续安装，安装成功后自动清理。不备份旧程序，保留游戏库、密钥、字体和译文。安装时工具会重启。</p>`;
  clearTimeout(rikaUpdatePoll);
  if (s.busy) rikaUpdatePoll = setTimeout(() => loadOnlineUpdates(panel), 900);
}

async function loadOnlineUpdates(expectedPanel) {
  const panel = document.getElementById('online-update-panel');
  if (!panel || (expectedPanel && panel!==expectedPanel) || state.updateInProgress) return;
  try {
    const status = await api('/api/updates/online/status');
    if (panel===document.getElementById('online-update-panel')) drawOnlineUpdates(status);
  } catch (error) {
    if (panel===document.getElementById('online-update-panel')) panel.innerHTML = notice(esc(error.message),'error');
  }
}

function showOnlineUpdateConfirmation(preview) {
  if (state.dirty) { toast('请先保存当前设置，再开始更新','info'); return; }
  modal('确认在线更新','完整程序包已下载并校验通过。',`
    <div class="update-versions"><div><small>当前版本</small><strong>v${esc(preview.currentVersion)}</strong></div><span class="update-arrow" aria-hidden="true">→</span><div><small>更新版本</small><strong>v${esc(preview.targetVersion)}</strong></div></div>
    <h3 class="update-notes-title">更新说明</h3><div class="update-notes">${esc(preview.releaseNotes)}</div>
    ${notice('请保存设置并关闭正在翻译的游戏。工具将安装新版后重新打开；保留个人数据，不创建旧程序备份。')}
    <div class="form-actions"><button class="btn" data-action="close-modal">稍后安装</button><button class="btn primary" data-action="install-local-update" data-id="${esc(preview.id)}">确认更新并重启</button></div>`);
}

document.addEventListener('click', async event => {
  const button = event.target.closest('[data-online-action]');
  if (!button || button.disabled) return;
  const action = button.dataset.onlineAction;
  if (action==='install') { if (rikaOnlineState?.preview) showOnlineUpdateConfirmation(rikaOnlineState.preview); return; }
  await busy(button, async () => {
    if (action==='check') {
      const panel=document.getElementById('online-update-panel');
      if (state.dirty) { toast('请先保存当前设置，再检查更新','info'); return; }
      if (panel) panel.querySelector('[role=status]').textContent='正在检查 GitHub 新版本…';
      const status=await api('/api/updates/online/check',{method:'POST'});
      if (panel===document.getElementById('online-update-panel')) drawOnlineUpdates(status);
    } else if (action==='download' && rikaOnlineState?.release) {
      drawOnlineUpdates(await api('/api/updates/online/download',{method:'POST',body:{id:rikaOnlineState.release.id}}));
    } else if (action==='cancel' || action==='discard') {
      drawOnlineUpdates(await api('/api/updates/online/cancel',{method:'POST'}));
    }
  });
});
