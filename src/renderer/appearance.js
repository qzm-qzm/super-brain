'use strict';
window.initAppearance = (initial, onSaved) => {
  const presets = [
    {id:'cloud',name:'云雾白',background:'#F6F8FB',accent:'#48648E'},
    {id:'graphite',name:'石墨黑',background:'#20242C',accent:'#AEC6FF'},
    {id:'blue',name:'雾蓝',background:'#EAF2FA',accent:'#345F91'},
    {id:'cream',name:'奶油',background:'#FBF5E9',accent:'#866343'},
    {id:'pink',name:'浅樱',background:'#F8EEF1',accent:'#9B4D68'},
    {id:'sage',name:'鼠尾草',background:'#EEF3EE',accent:'#43684D'}
  ];
  const defaults = {background:presets[0].background,accent:presets[0].accent,image:'',imageTransparency:90};
  const validHex = value => /^#[0-9a-f]{6}$/i.test(value);
  const validImage = value => typeof value==='string' && /^data:image\/(png|jpeg|webp);base64,[a-z0-9+/=]+$/i.test(value) && value.length < 2900000;
  let preferences={...defaults,...initial},saveTimer,imageRequest=0,lastFocus=null,saveChain=Promise.resolve();
  const rgb=hex=>[1,3,5].map(i=>parseInt(hex.slice(i,i+2),16));
  const hex=channels=>'#'+channels.map(v=>Math.max(0,Math.min(255,Math.round(v))).toString(16).padStart(2,'0')).join('').toUpperCase();
  const mix=(a,b,t)=>hex(rgb(a).map((v,i)=>v*(1-t)+rgb(b)[i]*t));
  const luminance=color=>{const c=rgb(color).map(v=>{v/=255;return v<=.04045?v/12.92:((v+.055)/1.055)**2.4});return .2126*c[0]+.7152*c[1]+.0722*c[2]};
  const contrast=(a,b)=>{const x=luminance(a),y=luminance(b);return (Math.max(x,y)+.05)/(Math.min(x,y)+.05)};
  function legible(color,background,target=7){
    const extreme=contrast('#000000',background)>contrast('#FFFFFF',background)?'#000000':'#FFFFFF';
    if(contrast(color,background)>=target)return color;
    for(let i=1;i<=100;i++){const c=mix(color,extreme,i/100);if(contrast(c,background)>=target)return c}
    return extreme;
  }
  const root=document.documentElement;
  const dialog=document.createElement('dialog');
  dialog.id='appearance-dialog';dialog.className='appearance-dialog';dialog.setAttribute('aria-labelledby','appearance-title');
  dialog.innerHTML=`
    <header class="appearance-titlebar"><h2 id="appearance-title">外观</h2><button class="icon-btn" id="appearance-close" aria-label="关闭外观设置">${icon('x')}</button></header>
    <div class="appearance-content">
      <p class="appearance-intro">选一种喜欢的配色，<br>让超强大脑更像你的空间。</p>
      <section class="appearance-section" aria-labelledby="themes-label"><h3 id="themes-label">预设主题</h3><div class="theme-presets">${presets.map(p=>`<button class="theme-preset" data-preset="${p.id}" aria-label="${p.name}" aria-pressed="false" style="--theme-swatch-bg:${p.background};--theme-swatch-accent:${p.accent}"><span class="theme-sample" aria-hidden="true"><span class="theme-sample-dot"></span><span class="theme-sample-line"></span></span>${p.name}<span class="theme-selected">${icon('check')}</span></button>`).join('')}</div></section>
      <section class="appearance-section" aria-labelledby="custom-label"><h3 id="custom-label">自定义颜色</h3>
        <div class="color-row"><label class="color-label" for="background-color">小窗背景</label><div class="color-control"><input class="color-picker" type="color" id="background-color" aria-label="选择小窗背景颜色"><input class="color-hex" id="background-hex" aria-label="小窗背景色值" aria-describedby="color-help" maxlength="7" spellcheck="false" autocomplete="off"></div></div>
        <div class="color-row"><label class="color-label" for="accent-color">主题颜色</label><div class="color-control"><input class="color-picker" type="color" id="accent-color" aria-label="选择主题颜色"><input class="color-hex" id="accent-hex" aria-label="主题颜色色值" aria-describedby="color-help" maxlength="7" spellcheck="false" autocomplete="off"></div></div>
        <p class="color-help" id="color-help">文字与按钮自动适配，保持清晰。</p>
      </section>
      <section class="appearance-section" aria-labelledby="wallpaper-label"><h3 id="wallpaper-label">背景图片 <span class="color-help">可选</span></h3><div class="wallpaper-actions"><button class="secondary" id="choose-wallpaper">选择本地图片</button><button class="text-btn" id="remove-wallpaper" hidden>移除</button></div><input type="file" id="wallpaper-file" accept="image/png,image/jpeg,image/webp" hidden><p class="color-help" id="wallpaper-help" role="status">支持 PNG、JPG、WebP，最大 2 MB。</p><div class="wallpaper-transparency"><div class="color-row"><label class="color-label" for="wallpaper-transparency">图片透明度</label><output id="transparency-value" for="wallpaper-transparency">90%</output></div><input type="range" id="wallpaper-transparency" min="0" max="100" step="1" value="90" aria-describedby="transparency-help" disabled><div class="range-endpoints" aria-hidden="true"><span>0% · 不透明</span><span>100% · 完全透明</span></div><p class="color-help" id="transparency-help">选择图片后可调节，只影响背景图片。</p></div></section>
    </div>
    <footer class="appearance-foot"><span class="appearance-save" id="appearance-save" role="status">${icon('check')}外观已保存</span><button class="text-btn" id="reset-appearance">恢复默认</button></footer>`;
  document.body.append(dialog);
  const el=id=>document.getElementById(id);
  function status(message){el('appearance-save').innerHTML=`${icon('check')}<span>${esc(message)}</span>`}
  function applyTheme(){
    const bg=preferences.background;
    const ink=legible('#18212D',bg,12);
    const dark=contrast('#FFFFFF',bg)>contrast('#000000',bg);
    const surfaceDirection=dark?'#000000':'#FFFFFF';
    const muted=legible(mix(bg,ink,.64),bg,7.8);
    const accent=legible(preferences.accent,bg,7.8);
    const onAccent=contrast('#FFFFFF',accent)>contrast('#000000',accent)?'#FFFFFF':'#000000';
    const stage=mix(bg,surfaceDirection,.18);
    const scrim=rgb(bg).join(' ');
    const tokens={
      '--color-stage':stage,'--color-paper':bg,'--color-surface':mix(bg,surfaceDirection,.07),
      '--color-hover':mix(bg,surfaceDirection,.14),'--color-ink':ink,'--color-muted':muted,
      '--color-rule':mix(bg,ink,.15),'--color-control':legible(mix(bg,ink,.38),bg,3.4),
      '--color-accent':accent,'--color-accent-soft':mix(bg,surfaceDirection,.16),
      '--color-on-accent':onAccent,'--color-error':legible('#AE3939',bg,6),
      '--color-shadow':dark?'rgb(0 0 0 / .3)':'rgb(20 30 45 / .16)',
      '--color-image-wash':`rgb(${scrim} / ${preferences.image?preferences.imageTransparency/100:1})`,
      '--color-modal-scrim':'rgb(5 10 20 / .4)',
      '--background-image':preferences.image?`url("${preferences.image}")`:'none'
    };
    Object.entries(tokens).forEach(([name,value])=>root.style.setProperty(name,value));
    root.style.colorScheme=dark?'dark':'light';
    dialog.querySelectorAll('[data-preset]').forEach(button=>{const p=presets.find(p=>p.id===button.dataset.preset);button.setAttribute('aria-pressed',String(p.background===preferences.background&&p.accent===preferences.accent))});
    for(const field of ['background','accent']){el(field+'-color').value=preferences[field];if(document.activeElement!==el(field+'-hex'))el(field+'-hex').value=preferences[field]}
    el('remove-wallpaper').hidden=!preferences.image;
    el('choose-wallpaper').textContent=preferences.image?'更换图片':'选择本地图片';
    el('wallpaper-transparency').disabled=!preferences.image;
    el('wallpaper-transparency').value=preferences.imageTransparency;
    el('wallpaper-transparency').setAttribute('aria-valuetext',`${preferences.imageTransparency}% 透明`);
    el('transparency-value').value=`${preferences.imageTransparency}%`;
    el('transparency-help').textContent=preferences.image?'数字越大，图片越淡；不影响文字。':'选择图片后可调节，只影响背景图片。';
  }
  function persist(){
    clearTimeout(saveTimer);saveTimer=null;
    const snapshot=structuredClone(preferences);
    saveChain=saveChain.catch(()=>{}).then(()=>window.brain.saveSettings(snapshot)).then(saved=>{onSaved(saved);status('外观已保存');return saved;}).catch(error=>{status('保存失败：'+error.message);throw error;});
    saveChain.catch(()=>{});
    return saveChain;
  }
  function changed(){applyTheme();status('正在保存…');clearTimeout(saveTimer);saveTimer=setTimeout(persist,180)}
  function openAppearance(){if(dialog.open)return;lastFocus=document.activeElement;applyTheme();document.body.classList.add('appearance-open');dialog.showModal();el('appearance-close').focus()}
  async function closeAppearance(){if(!dialog.open)return;try{await persist();}catch{return;}dialog.close();document.body.classList.remove('appearance-open');if(lastFocus?.isConnected)lastFocus.focus()}
  document.addEventListener('click',event=>{if(event.target.closest('[data-appearance-open]'))openAppearance()});
  el('appearance-close').onclick=closeAppearance;
  dialog.addEventListener('cancel',event=>{event.preventDefault();closeAppearance()});
  document.addEventListener('keydown',event=>{
    if(!dialog.open||event.isComposing||event.keyCode===229)return;
    if(event.key==='Escape'){event.preventDefault();event.stopImmediatePropagation();closeAppearance()}
    else if(event.altKey&&event.code==='KeyQ'){event.preventDefault();event.stopImmediatePropagation();closeAppearance().then(()=>window.appHide())}
  },true);
  dialog.querySelectorAll('[data-preset]').forEach(button=>button.onclick=()=>{
    const p=presets.find(p=>p.id===button.dataset.preset);preferences.background=p.background;preferences.accent=p.accent;
    clearErrors();changed();
  });
  function clearErrors(){el('color-help').textContent='文字与按钮自动适配，保持清晰。';el('color-help').classList.remove('error');['background','accent'].forEach(field=>el(field+'-hex').removeAttribute('aria-invalid'))}
  for(const field of ['background','accent']){
    el(field+'-color').addEventListener('input',event=>{preferences[field]=event.target.value.toUpperCase();el(field+'-hex').value=preferences[field];clearErrors();changed()});
    const input=el(field+'-hex');
    input.addEventListener('input',()=>{let value=input.value.trim();if(/^[0-9a-f]{6}$/i.test(value))value='#'+value;if(validHex(value)){preferences[field]=value.toUpperCase();clearErrors();changed()}});
    input.addEventListener('blur',()=>{let value=input.value.trim();if(/^[0-9a-f]{6}$/i.test(value))value='#'+value;if(validHex(value)){input.value=preferences[field];clearErrors()}else{input.setAttribute('aria-invalid','true');el('color-help').textContent='请输入六位颜色值，例如 #F6F8FB。';el('color-help').classList.add('error')}});
  }
  el('choose-wallpaper').onclick=()=>el('wallpaper-file').click();
  el('wallpaper-transparency').addEventListener('input',event=>{
    preferences.imageTransparency=Number(event.target.value);
    changed();
  });
  el('wallpaper-file').onchange=async event=>{
    const file=event.target.files[0];event.target.value='';if(!file)return;
    const request=++imageRequest;
    const help=el('wallpaper-help');
    if(!['image/png','image/jpeg','image/webp'].includes(file.type)){help.textContent='请选择 PNG、JPG 或 WebP 图片。';return}
    if(file.size>2*1024*1024){help.textContent='这张图片超过 2 MB，请换一张小一点的图片。';return}
    el('choose-wallpaper').disabled=true;el('choose-wallpaper').textContent='读取图片…';
    try{
      const data=await new Promise((resolve,reject)=>{const reader=new FileReader();reader.onload=()=>resolve(reader.result);reader.onerror=reject;reader.readAsDataURL(file)});
      if(!validImage(data))throw Error('invalid image');
      const image=new Image();image.src=data;await image.decode();
      if(request!==imageRequest)return;
      preferences.image=data;help.textContent='图片已载入，可用下方滑块调整透明度。';changed();
    }catch{if(request===imageRequest)help.textContent='无法读取这张图片，请换一张 PNG、JPG 或 WebP。'}
    finally{el('choose-wallpaper').disabled=false;applyTheme()}
  };
  el('remove-wallpaper').onclick=()=>{++imageRequest;preferences.image='';el('wallpaper-help').textContent='支持 PNG、JPG、WebP，最大 2 MB。';changed()};
  el('reset-appearance').onclick=()=>{++imageRequest;preferences={...preferences,...defaults};clearErrors();el('wallpaper-help').textContent='支持 PNG、JPG、WebP，最大 2 MB。';changed()};
  window.appearance={open:openAppearance,flush:()=>saveTimer?persist():saveChain,update:value=>{preferences={...preferences,...value};applyTheme();}};
  applyTheme();
};
