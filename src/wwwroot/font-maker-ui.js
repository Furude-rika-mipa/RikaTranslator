'use strict';

let fontMakerDraft = {sourcePath:'',profile:'2021',characterSet:'common',customCharacters:'',samplingSize:48,atlasSize:4096,padding:5,gameId:''};
let fontMakerPoll = null;
let fontMakerTemplates = [];
let fontMakerBusy = false;

function fontMakerGameOptions(value) {
  return '<option value="">不指定游戏</option>' + state.games.filter(g=>g.engine==='Unity').map(g=>`<option value="${esc(g.id)}" ${g.id===value?'selected':''}>${esc(g.name)}${g.engineVersion?' · Unity '+esc(g.engineVersion):''}</option>`).join('');
}
function fontMakerVersionHint() {
  const p=fontMakerTemplates.find(t=>t.id===document.querySelector('#font-maker-form [name=profile]')?.value);
  const target=document.getElementById('font-maker-version-hint');
  if(target) target.textContent=p?`模板由 Unity ${p.unityVersion} 生成，TMP 资产版本 ${p.tmpVersion}。同一系列的其他版本仍需在游戏中验证。`:'';
}
function rememberFontMakerForm() {
  const f=document.getElementById('font-maker-form'); if(!f) return;
  for(const name of ['sourcePath','profile','characterSet','customCharacters','gameId']) fontMakerDraft[name]=f.elements[name].value;
  for(const name of ['samplingSize','atlasSize','padding']) fontMakerDraft[name]=Number(f.elements[name].value);
}
async function selectFontMakerGame(form) {
  const selection=form._gameVersionSelection=(form._gameVersionSelection||0)+1;
  const id=form.elements.gameId.value;
  let game=gameById(id);
  const hint=document.getElementById('font-maker-game-hint');
  const start=form.querySelector('[type=submit]');
  delete form.dataset.checkingVersion;if(start)start.disabled=fontMakerBusy;
  if(game&&!game.engineVersion) {
    form.dataset.checkingVersion=id;if(start)start.disabled=true;
    if(hint)hint.textContent='正在重新读取游戏版本…';
    try {
      const detected=await api(`/api/games/${encodeURIComponent(id)}/recheck`,{method:'POST'});
      const saved=gameById(id);if(saved)Object.assign(saved,detected);
      game=saved;
      if(form.isConnected) {
        const option=[...form.elements.gameId.options].find(option=>option.value===id);
        if(option&&game)option.textContent=game.name+(game.engineVersion?' · Unity '+game.engineVersion:'');
      }
    } catch(error) {
      if(form.isConnected&&form._gameVersionSelection===selection)toast(error.message,'error');
    }
  }
  if(!form.isConnected||form._gameVersionSelection!==selection||form.elements.gameId.value!==id)return;
  delete form.dataset.checkingVersion;if(start)start.disabled=fontMakerBusy;
  const major=(game?.engineVersion||'').match(/^(\d{4})\./)?.[1];
  if(major&&fontMakerTemplates.some(template=>template.id===major)) {
    form.elements.profile.value=major;
    if(hint)hint.textContent=`已识别 Unity ${game.engineVersion}，已选择对应系列模板。具体兼容性请核对下方模板版本。`;
  } else if(hint)hint.textContent=game?(game.engineVersion?'暂未提供这个 Unity 系列的模板，请核对兼容性后手动选择。':'未能读取游戏的 Unity 版本，请手动选择模板。'):'游戏版本已识别时，会帮你选择对应系列的模板。';
  rememberFontMakerForm();fontMakerVersionHint();
}
async function renderFontMaker(sequence=renderSequence) {
  clearTimeout(fontMakerPoll);
  const [templates,status,results]=await Promise.all([api('/api/font-maker/templates'),api('/api/font-maker/status'),api('/api/font-maker/results')]);
  if(sequence!==renderSequence) return;
  fontMakerTemplates=templates;
  if(state.gameId&&gameById(state.gameId)?.engine==='Unity'&&fontMakerDraft.gameId!==state.gameId){
    fontMakerDraft.gameId=state.gameId;
    const major=(gameById(state.gameId).engineVersion||'').match(/^(\d{4})\./)?.[1];
    if(major&&templates.some(t=>t.id===major))fontMakerDraft.profile=major;
  }
  main.innerHTML=`${pageHead('字体制作','把本机 TTF / OTF 字体制作成供 Unity 游戏使用的 TMP 字体包。',`<button class="btn" data-font-maker="folder">${icon('folder')}打开成品目录</button>`)}
    <div class="grid-two"><section class="card"><div class="card-header"><h2>${icon('edit')}制作 TMP 字体</h2></div>
    <form id="font-maker-form">
      <label class="field">字体文件<div class="inline-input"><input name="sourcePath" value="${esc(fontMakerDraft.sourcePath)}" placeholder="选择 TTF 或 OTF 文件" required><button class="btn" type="button" data-font-maker="pick">选择文件</button></div><small>制作在本机完成，不需要 Unity 编辑器，也不调用翻译服务。</small></label>
      <label class="field">目标游戏（可选）<select name="gameId">${fontMakerGameOptions(fontMakerDraft.gameId)}</select><small id="font-maker-game-hint">游戏版本已识别时，会帮你选择对应系列的模板。</small></label>
      <label class="field">Unity 模板<select name="profile">${templates.map(t=>`<option value="${esc(t.id)}" ${t.id===fontMakerDraft.profile?'selected':''}>${esc(t.label)}</option>`).join('')}</select><small id="font-maker-version-hint"></small></label>
      <label class="field">字符集<select name="characterSet"><option value="common" ${fontMakerDraft.characterSet==='common'?'selected':''}>常用简中 + 日文、数字、标点</option><option value="cjk" ${fontMakerDraft.characterSet==='cjk'?'selected':''}>基本汉字（含繁体）+ 日文、数字、标点</option><option value="custom" ${fontMakerDraft.characterSet==='custom'?'selected':''}>自定义字符（自动包含英文、数字）</option></select></label>
      <label class="field">补充 / 自定义字符<textarea name="customCharacters" rows="3" maxlength="65536" placeholder="可以粘贴需要包含的人名、生僻字或符号">${esc(fontMakerDraft.customCharacters)}</textarea><small>只会生成原字体本身包含的字符；缺失字符会列在制作结果里。</small></label>
      <details class="advanced"><summary>图集与清晰度</summary><div class="field-row section-gap"><label class="field">采样大小<input name="samplingSize" type="number" min="24" max="96" step="1" required value="${fontMakerDraft.samplingSize}"><small>默认 48。数值越大，字形细节越多，占用也越大。</small></label><label class="field">每页图集<select name="atlasSize">${[1024,2048,4096].map(n=>`<option value="${n}" ${n===fontMakerDraft.atlasSize?'selected':''}>${n} × ${n}</option>`).join('')}</select><small>字形排满后自动增加图集，最多 4 页。</small></label></div><label class="field">字形留白<input name="padding" type="number" min="2" max="12" step="1" required value="${fontMakerDraft.padding}"></label></details>
      <div class="form-actions"><button class="btn primary" type="submit" id="font-maker-start">${icon('play')}开始制作</button></div>
    </form></section><div class="stack"><section class="card"><div class="card-header"><h2>${icon('clock')}制作进度</h2></div><div id="font-maker-status"></div></section><section class="card"><div class="card-header"><h2>${icon('info')}使用方式</h2></div><p class="card-description">制作完成后，可导出字体包，或选择游戏并打开字体设置。优先选择 TMP 后备字体，保留游戏原字体已有的数字和符号。</p><p class="tiny subtle section-gap">字体包的字符和结构会经过文件检查，游戏里的显示效果仍需验证。这里只制作字体，不会自动修改游戏文件。</p><div class="actions section-gap"><button class="btn" type="button" data-font-maker="folder">${icon('folder')}打开字体文件夹</button></div></section></div></div>
    <section class="card section-gap"><div class="card-header"><h2>${icon('folder')}已制作的字体</h2><button class="btn ghost small" data-font-maker="refresh">${icon('refresh')}刷新</button></div><div id="font-maker-results"></div></section>`;
  fontMakerVersionHint(); drawFontMakerStatus(status); drawFontMakerResults(results);
  const form=document.getElementById('font-maker-form');
  form.addEventListener('input',rememberFontMakerForm);
  form.addEventListener('change',event=>{
    if(event.target.name==='gameId') {
      void selectFontMakerGame(form);
    }
    rememberFontMakerForm(); fontMakerVersionHint();
  });
  if(form.elements.gameId.value&&!gameById(form.elements.gameId.value)?.engineVersion)void selectFontMakerGame(form);
  form.onsubmit=async event=>{
    event.preventDefault(); if(!form.reportValidity()||fontMakerBusy||form.dataset.checkingVersion) return;
    rememberFontMakerForm();
    await busy(form.querySelector('[type=submit]'),async()=>{
      const {gameId,...request}=fontMakerDraft;
      await api('/api/font-maker/start',{method:'POST',body:request});
      state.dirty=false; await pollFontMaker();
    });
    if(fontMakerBusy) document.getElementById('font-maker-start').disabled=true;
  };
  if(status.busy)fontMakerPoll=setTimeout(pollFontMaker,700);
}
function drawFontMakerStatus(status) {
  const panel=document.getElementById('font-maker-status'); if(!panel) return;
  fontMakerBusy=!!status.busy;
  const p=status.progress||{},ratio=p.total?Math.min(1,p.current/p.total):0;
  panel.innerHTML=`<p class="card-description" role="status">${esc(p.message||'等待开始制作。')}</p>${status.busy?`<progress class="online-update-progress" max="1" value="${ratio}" aria-label="字体制作进度"></progress><div class="actions section-gap"><button class="btn" data-font-maker="cancel">取消制作</button></div>`:''}${status.error?notice(esc(status.error),'error'):''}${status.result&&!status.busy?`<p class="section-gap">${badge('已完成','green')} ${esc(status.result.fontName)} · ${number(status.result.characters)} 个字符</p>${status.result.missingCount?notice(`原字体缺少 ${number(status.result.missingCount)} 个所选字符，请查看下方结果。`,'warning'):''}`:''}`;
  const start=document.getElementById('font-maker-start');if(start)start.disabled=fontMakerBusy||!!document.getElementById('font-maker-form')?.dataset.checkingVersion;
}
function drawFontMakerResults(results) {
  const holder=document.getElementById('font-maker-results'); if(!holder) return;
  if(!results.length){holder.innerHTML='<p class="card-description">尚未制作字体，完成的字体包会保留在这里。</p>';return;}
  holder.innerHTML=results.slice(0,30).map(r=>`<article class="font-maker-result"><div class="subheading"><h3>${esc(r.fontName)}</h3>${badge(r.profile==='6000'?'Unity 6':'Unity '+r.profile,'blue')}</div><p class="tiny subtle">${esc(dateText(r.createdAt))} · 模板 Unity ${esc(r.unityVersion)} · ${number(r.characters)} 个字符 / ${number(r.glyphs)} 个字形 · ${r.atlasCount} 页 ${r.atlasSize}² · ${updateSize(r.bytes)}</p>${r.missingCount?`<details class="font-report"><summary>原字体缺少 ${number(r.missingCount)} 个所选字符</summary><p class="font-maker-missing">${esc(r.missingSample)}</p><small>最多展示 100 个；增加图集大小无法补上原字体没有的字。</small></details>`:''}<div class="actions section-gap"><button class="btn" data-font-maker="download" data-id="${esc(r.id)}">${icon('download')}导出字体包</button><button class="btn" data-font-maker="use" data-id="${esc(r.id)}">用于选中的游戏</button></div><p class="tiny subtle section-gap">${esc(r.note)}</p></article>`).join('');
}
async function pollFontMaker() {
  clearTimeout(fontMakerPoll);if(state.route!=='fonts')return;
  const panel=document.getElementById('font-maker-status');if(!panel)return;
  try{
    const status=await api('/api/font-maker/status');
    if(panel!==document.getElementById('font-maker-status'))return;
    drawFontMakerStatus(status);
    if(!status.busy)drawFontMakerResults(await api('/api/font-maker/results'));
    if(status.busy)fontMakerPoll=setTimeout(pollFontMaker,700);
  }catch(error){if(panel.isConnected)panel.innerHTML=notice(esc(error.message),'error');}
}
document.addEventListener('click',async event=>{
  const button=event.target.closest('[data-font-maker]');if(!button||button.disabled)return;
  const action=button.dataset.fontMaker;
  await busy(button,async()=>{
    if(action==='pick'){
      const result=await api('/api/font-maker/pick',{method:'POST'}),input=document.querySelector('#font-maker-form [name=sourcePath]');
      if(result.path&&input){input.value=result.path;rememberFontMakerForm();state.dirty=true;}
    }else if(action==='cancel'){await api('/api/font-maker/cancel',{method:'POST'});await pollFontMaker();}
    else if(action==='folder')await api('/api/font-maker/open-folder',{method:'POST'});
    else if(action==='download')await api(`/api/font-maker/${encodeURIComponent(button.dataset.id)}/download`,{download:true});
    else if(action==='refresh')drawFontMakerResults(await api('/api/font-maker/results'));
    else if(action==='use'){
      rememberFontMakerForm();const id=fontMakerDraft.gameId;
      if(!id){toast('请先在上方选择要使用字体的 Unity 游戏','info');return;}
      const selection=await api(`/api/font-maker/${encodeURIComponent(button.dataset.id)}/selection`);
      await showGameFont(id);
      const form=document.getElementById('game-font-form');if(!form)return;
      form.elements.mode.value='custom';form.elements.mode.dispatchEvent(new Event('change',{bubbles:true}));
      form.elements.filePath.value=selection.path;if(form.elements.clearFile)form.elements.clearFile.checked=false;
      form.elements.tmpMode.value='fallback';modalDirty=true;
      toast('已选入生成的字体包，确认设置后保存即可','info');
    }
  });
});
