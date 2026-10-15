'use strict';
const $=id=>document.getElementById(id);
const state={scope:'all',view:'app',japan:false,data:null,status:null,expanded:new Set(),limit:30,loading:false,request:0,csrf:'',auto:false,theme:'system',preferencesLoaded:false,portLoaded:false};
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function bytes(n,markup=false){n=Number(n)||0;const units=['B','KB','MB','GB','TB'];let i=0;while(n>=1024&&i<units.length-1){n/=1024;i++;}const value=n.toLocaleString('en-US',{maximumFractionDigits:i===0?0:2,minimumFractionDigits:i>=3?2:0});return markup?`${value}<small>${units[i]}</small>`:`${value} ${units[i]}`;}
const stamp=(seconds,day=false)=>new Intl.DateTimeFormat('zh-CN',{timeZone:'Asia/Shanghai',...(day?{month:'2-digit',day:'2-digit'}:{}),hour:'2-digit',minute:'2-digit',second:day?undefined:'2-digit',hour12:false}).format(new Date(seconds*1000));
const dateOf=d=>new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit'}).format(d);
function pressed(group,match){document.querySelectorAll(group+' button').forEach(b=>{const active=match(b);b.classList.toggle('active',active);b.setAttribute('aria-pressed',String(active));});}
function preset(days){let end=new Date(dateOf(new Date())+'T00:00:00+08:00'),start=new Date(end);if(days===2){start.setUTCDate(start.getUTCDate()-1);end=new Date(start);}else start.setUTCDate(start.getUTCDate()-days+1);$('date-from').value=dateOf(start);$('date-to').value=dateOf(end);pressed('#presets',b=>Number(b.dataset.days)===days);}
function params(){const q=new URLSearchParams({from:$('date-from').value,to:$('date-to').value,scope:state.scope,search:$('search').value.trim()});if(state.japan)q.set('japan','true');if($('node-filter').value)q.set('node',$('node-filter').value);return q;}
async function api(path,post=false){const response=await fetch(path,{method:post?'POST':'GET',cache:'no-store',headers:post?{'X-Ledger-Token':state.csrf}:{}});if(!response.ok){const text=await response.text();throw new Error(text||'读取失败');}return response.json();}
let toastTimer;
function toast(text){$('toast').textContent=text;$('toast').hidden=false;clearTimeout(toastTimer);toastTimer=setTimeout(()=>$('toast').hidden=true,5000);}
async function refresh(){
  const serial=++state.request;state.loading=true;$('refresh-button').disabled=true;
  try{const [status,data]=await Promise.all([api('/api/status'),api('/api/stats?'+params())]);if(serial!==state.request)return;state.status=status;state.csrf=status.csrf;state.data=data;renderStatus();renderData();$('last-refresh').textContent='更新于 '+stamp(status.now);}
  catch(error){if(serial===state.request){$('message').hidden=false;$('message').textContent=error.message;}}
  finally{if(serial===state.request){state.loading=false;$('refresh-button').disabled=false;}}
}
function renderStatus(){
  const s=state.status,c=s.collector,e=s.etw;
  $('capture-status').classList.toggle('off',!c.connected||!c.storageHealthy);
  $('capture-status').innerHTML='<i></i>'+esc(!c.storageHealthy?'历史写入异常':c.connected?`记录中 · ${c.connections} 个连接`:'Clash 未连接');
  $('startup-toggle').checked=s.startup;
  $('startup-repair-row').hidden=!s.startupNeedsRepair;
  if(!state.preferencesLoaded){state.theme=s.preferences?.theme||'system';state.auto=Boolean(s.preferences?.autoRefresh);state.preferencesLoaded=true;applyTheme();$('auto-refresh-toggle').checked=state.auto;}
  const endpoint=s.endpoint;
  if(endpoint){
    $('endpoint-description').textContent=`当前地址：${location.origin}。`+(endpoint.restartRequired?'已保存，请退出托盘后重新启动生效。':endpoint.fallback?'指定端口被占用，已自动使用空闲端口。':'从托盘打开，无需记住端口。');
    if(!state.portLoaded){$('port-mode').value=endpoint.automatic?'auto':'fixed';$('port-number').value=endpoint.configuredPort||18793;updatePortField();state.portLoaded=true;}
  }
  let windows=e.running?(e.suspended?'已启动，当前暂停（TUN 开启或主采集器离线）':'正在记录未经过 Clash 的应用流量'):s.fullRequested?'未在运行，请重新启用或查看记录状态':'尚未启用；需要一次 Windows 管理员授权';
  if(Number(e.error))windows+=` · 错误码 ${e.error}`;
  if(e.lostEvents||e.lostBuffers||e.unsupportedEvents||e.droppedFlows)windows+=' · 存在丢失事件，此时段不完整';
  $('windows-status').textContent=windows;$('enable-windows').textContent=e.running?'重新启用':'启用补充采集';
  $('storage-status').textContent=`账本 ${bytes(s.databaseBytes)} · 后台内存 ${bytes(s.memoryBytes)} · 明细保留 ${s.retentionDays} 天`;
  $('data-directory').textContent='数据位置：'+s.dataDirectory;
  $('windows-note').textContent=e.running&&!e.suspended?'正在记录 · IP 级统计':e.suspended?'当前暂停，避免 TUN 重复计量':'补充采集未运行';
  $('footer-status').textContent=`v${s.version} · ${c.version?'Mihomo '+c.version:'等待 Clash'} · 页面关闭后继续记录`;
  const notices=[];if(!c.storageHealthy)notices.push('历史写入失败。请检查磁盘空间；当前统计可能不完整。');if(!c.connected)notices.push('暂未连接 Clash Verge，历史记录仍可查看。'+c.error);if(c.tun)notices.push('检测到 TUN 已开启，Windows 补充计量会暂停，以免重复记账。');if(state.data.truncated)notices.push('明细组合超过 5,000 条，当前展示流量最高的 5,000 条；摘要总量包含全部记录。请缩小日期或增加筛选。');
  $('message').hidden=!notices.length;$('message').textContent=notices.join(' ');
}
function renderData(){
  const d=state.data,s=d.summary,total=Number(s.upload)+Number(s.download);
  $('total-value').innerHTML=bytes(total,true);$('total-upload').textContent='↑ '+bytes(s.upload);$('total-download').textContent='↓ '+bytes(s.download);
  let proxy=0,direct=0,windows=0;d.kinds.forEach(k=>{const n=Number(k.upload)+Number(k.download);if(k.source==='windows')windows+=n;else if(k.route==='PROXY')proxy+=n;else if(k.route==='DIRECT')direct+=n;});
  $('proxy-value').innerHTML=bytes(proxy,true);$('direct-value').innerHTML=bytes(direct,true);$('windows-value').innerHTML=windows===0&&!state.status.etw.running?'—':bytes(windows,true);$('proxy-note').textContent=state.japan?'按日本节点名称筛选':'按连接的实际节点统计';
  const core=Number(d.core.upload)+Number(d.core.download),attributed=Number(d.core.attributedUpload)+Number(d.core.attributedDownload);const missing=Math.max(0,core-attributed);
  $('accuracy-text').textContent=`所选日期内，全 Clash 内核累计 ${bytes(core)} · 未归属差额参考 ${bytes(missing)}。各来源按各自口径计量。`;
  const selected=$('node-filter').value;$('node-filter').innerHTML='<option value="">全部节点</option>'+d.nodes.map(n=>`<option value="${esc(n)}">${esc(n)}</option>`).join('');if(d.nodes.includes(selected))$('node-filter').value=selected;
  $('range-summary').textContent=$('date-from').value+' — '+$('date-to').value+' · 北京时间';
  $('inventory-summary').textContent=`${Number(s.applications)||0} 个应用 · ${Number(s.hosts)||0} 个域名 / IP`;
  const filters=[];if(state.scope!=='all')filters.push(({proxy:'代理',direct:'Clash 直连',windows:'Clash 外'})[state.scope]);if(state.japan)filters.push('日本节点');if(selected)filters.push(selected);if($('search').value.trim())filters.push('搜索：'+$('search').value.trim());
  $('filter-summary').hidden=!filters.length;$('active-filters').textContent=filters.join(' · ');
  renderChart();renderComposition(proxy,direct,windows,total);renderTable();renderHistory();
}
function renderChart(){
  const d=state.data,raw=d.trend,step=Number(d.trendStep);$('trend-subtitle').textContent=`按 ${step>=3600?'小时':step/60+' 分钟'}汇总 · 空白处无记录`;
  if(!raw.length){$('trend-chart').innerHTML='<div class="chart-empty"><span>这段时间还没有流量记录</span><small>后台开始采集后，用量会逐渐出现在这里。</small></div>';$('trend-peak').textContent='暂无峰值记录';return;}
  const width=Math.max(280,$('trend-chart').clientWidth-30),height=225,left=58,right=12,top=20,bottom=30,plotH=height-top-bottom,plotW=width-left-right;
  const from=Number(d.from),to=Number(d.to),peak=Math.max(1,...raw.map(r=>Math.max(Number(r.upload),Number(r.download))));
  const scale=10**Math.floor(Math.log10(peak)),max=Math.ceil(peak/scale)*scale;
  const x=t=>left+(t-from)/(to-from)*plotW,y=n=>top+plotH-(n/max)*plotH;
  let svg=`<svg viewBox="0 0 ${width} ${height}" role="group" aria-label="上下行用量趋势。Tab 进入数据点，左右方向键切换。缺失时段不连线。"><defs><linearGradient id="download-area" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stop-color="var(--blue)" stop-opacity=".18"/><stop offset="100%" stop-color="var(--blue)" stop-opacity=".01"/></linearGradient></defs>`;
  for(let i=0;i<4;i++){const n=max*(1-i/3),yy=y(n);svg+=`<line class="chart-grid" x1="${left}" x2="${width-right}" y1="${yy}" y2="${yy}"/><text class="chart-text" x="${left-9}" y="${yy+3}" text-anchor="end">${bytes(n)}</text>`;}
  ['download','upload'].forEach(field=>{const segments=[];let previous=-Infinity;raw.forEach(r=>{const t=Number(r.bucket);if(t-previous>step*1.5)segments.push([]);segments.at(-1).push([x(t),y(Number(r[field]))]);previous=t;});segments.forEach(points=>{const path=points.map((p,i)=>(i?'L':'M')+p.map(n=>n.toFixed(1)).join(',')).join(' ');if(field==='download')svg+=`<path fill="url(#download-area)" d="${path} L${points.at(-1)[0]},${y(0)} L${points[0][0]},${y(0)} Z"/>`;svg+=`<path class="chart-line ${field}-line" d="${path}"/>`;});});
  raw.forEach((r,i)=>{const xx=x(Number(r.bucket)),yy=y(Number(r.download)),label=`${stamp(Number(r.bucket),true)}，下载 ${bytes(r.download)}，上传 ${bytes(r.upload)}`;svg+=`<circle cx="${xx.toFixed(1)}" cy="${yy.toFixed(1)}" r="2" fill="var(--blue)"/><rect class="chart-focus" data-point="${i}" data-x="${xx}" data-width="${width}" x="${Math.max(left,xx-plotW*step/(to-from)/2)}" y="${top}" width="${Math.max(2,plotW*step/(to-from))}" height="${plotH}" tabindex="${i===0?0:-1}" role="img" aria-label="${esc(label)}" fill-opacity=".08"><title>${esc(label)}</title></rect>`;});
  for(let i=0;i<5;i++){const t=from+(to-from)*i/4;const label=to-from>86400?new Intl.DateTimeFormat('zh-CN',{timeZone:'Asia/Shanghai',month:'2-digit',day:'2-digit'}).format(new Date((t-(i===4?1:0))*1000)):stamp(t-(i===4?1:0)).slice(0,5);svg+=`<text class="chart-text" x="${x(t)}" y="${height-5}" text-anchor="${i===0?'start':i===4?'end':'middle'}">${esc(label)}</text>`;}
  svg+='<g id="chart-tooltip" class="chart-tooltip" hidden pointer-events="none"><rect width="164" height="58" rx="7"/><text x="11" y="17" id="tooltip-time"></text><text x="11" y="33" id="tooltip-download"></text><text x="11" y="48" id="tooltip-upload"></text></g>';
  $('trend-chart').innerHTML=svg+'</svg>';const largest=raw.reduce((a,b)=>Number(a.upload)+Number(a.download)>Number(b.upload)+Number(b.download)?a:b);$('trend-peak').textContent=`单时段峰值 ${bytes(Number(largest.upload)+Number(largest.download))} · ${stamp(Number(largest.bucket))}`;
}
function chartTooltip(target){const point=target.closest('[data-point]');if(!point)return;const r=state.data.trend[Number(point.dataset.point)],tip=$('chart-tooltip');tip.removeAttribute('hidden');tip.setAttribute('transform',`translate(${Math.max(5,Math.min(Number(point.dataset.width)-169,Number(point.dataset.x)-82))},2)`);$('tooltip-time').textContent=stamp(Number(r.bucket),true);$('tooltip-download').textContent='↓ 下载 '+bytes(r.download);$('tooltip-upload').textContent='↑ 上传 '+bytes(r.upload);}
$('trend-chart').addEventListener('pointerover',e=>chartTooltip(e.target));$('trend-chart').addEventListener('focusin',e=>chartTooltip(e.target));
function hideChartTooltip(){if($('chart-tooltip'))$('chart-tooltip').setAttribute('hidden','');}
$('trend-chart').addEventListener('pointerleave',hideChartTooltip);$('trend-chart').addEventListener('focusout',hideChartTooltip);
$('trend-chart').addEventListener('keydown',e=>{if(e.key==='Escape')hideChartTooltip();if(!['ArrowLeft','ArrowRight','Home','End'].includes(e.key)||!e.target.hasAttribute('data-point'))return;e.preventDefault();const points=[...$('trend-chart').querySelectorAll('[data-point]')],i=Number(e.target.dataset.point),next=e.key==='Home'?0:e.key==='End'?points.length-1:Math.max(0,Math.min(points.length-1,i+(e.key==='ArrowLeft'?-1:1)));e.target.setAttribute('tabindex','-1');points[next].setAttribute('tabindex','0');points[next].focus();});
function renderComposition(proxy,direct,windows,total){
  const sources=[{name:'通过代理',n:proxy,color:'blue'},{name:'Clash 直连',n:direct,color:'teal'},{name:'Clash 外',n:windows,color:'amber'}];if(total-proxy-direct-windows>0)sources.push({name:'其他路线',n:total-proxy-direct-windows,color:'muted'});
  let offset=0;const segments=sources.map(s=>{const percentage=total?s.n/total*100:0;const segment=`<circle class="segment" cx="60" cy="60" r="46" pathLength="100" stroke="var(--${s.color})" stroke-dasharray="${percentage} ${100-percentage}" stroke-dashoffset="${-offset}"/>`;offset+=percentage;return segment;}).join('');
  $('source-chart').innerHTML=`<svg viewBox="0 0 120 120" role="img" aria-label="流量来源占比"><circle class="source-track" cx="60" cy="60" r="46" stroke-width="10"/>${segments}</svg><div class="source-center">${total?(proxy/total*100).toFixed(0)+'%':'—'}<small>代理占比</small></div>`;
  $('source-legend').innerHTML=sources.map(s=>`<div class="source-item"><span><i class="dot ${s.color}"></i>${s.name}</span><span>${total?(s.n/total*100).toFixed(1):'0.0'}%</span></div>`).join('');
  const sites=groupRows(state.data.rows,r=>service(r.host));$('sites-caption').textContent=state.data.truncated?'前 5,000 组内的排行':'按用量排序';$('top-sites').innerHTML=sites.length?sites.slice(0,3).map((s,i)=>`<div class="top-site"><span class="site-rank">0${i+1}</span><span class="site-name" title="${esc(s.key)}">${esc(s.key||'未知网站')}</span><span class="site-amount">${bytes(s.upload+s.download)}</span></div>`).join(''):'<p class="sites-empty">有记录后，主要流量去向会出现在这里。</p>';
}
function service(host){host=host.toLowerCase();const match=s=>host===s||host.endsWith('.'+s);if(['youtube.com','youtu.be','googlevideo.com','ytimg.com','youtube-nocookie.com'].some(match))return 'YouTube';if(['chatgpt.com','openai.com','oaistatic.com','oaiusercontent.com'].some(match))return 'ChatGPT / OpenAI';if(['github.com','githubassets.com','githubusercontent.com'].some(match))return 'GitHub';if(['claude.ai','anthropic.com'].some(match))return 'Claude / Anthropic';return host;}
function groupRows(rows,keyFn){const groups=new Map();rows.forEach(r=>{const key=keyFn(r);if(!groups.has(key))groups.set(key,{key,rows:[],upload:0,download:0});const g=groups.get(key);g.rows.push(r);g.upload+=Number(r.upload);g.download+=Number(r.download);});return [...groups.values()].sort((a,b)=>(b.upload+b.download)-(a.upload+a.download));}
function routeTag(r){return r.source==='windows'?'<span class="route-tag windows">Clash 外</span>':r.route==='DIRECT'?'<span class="route-tag direct">直连</span>':'<span class="route-tag">代理</span>';}
function rowHtml(g,key,title,subtitle,level,icon,expandable,tag='',applicationId=''){
  const total=Number(state.data.summary.upload)+Number(state.data.summary.download),share=total?(g.upload+g.download)/total*100:0,open=state.expanded.has(key);
  return `<div class="traffic-row ${level===1?'child':level===2?'grandchild':''} ${expandable?'expandable':''} ${open?'expanded':''}" ${expandable?`data-expand="${esc(key)}" role="button" tabindex="0" aria-expanded="${open}"`:''}><div class="row-identity">${expandable?`<span class="chevron" aria-hidden="true">${open?'⌄':'›'}</span>`:''}${level===2?'':`<span class="app-icon ${icon}"><span class="app-icon-fallback" aria-hidden="true">${esc(icon==='node'?'↗':icon==='domain'?'◈':title.slice(0,1).toUpperCase())}</span>${applicationId?`<img data-icon-id="${esc(applicationId)}" alt="" width="48" height="48" decoding="async">`:''}</span>`}<div class="identity-text"><div class="row-title" title="${esc(title)}">${esc(title)}${tag}</div><div class="row-subtitle" title="${esc(subtitle)}">${esc(subtitle)}</div></div></div><span class="number total">${bytes(g.upload+g.download)}</span><span class="number">${bytes(g.upload)}</span><span class="number">${bytes(g.download)}</span><span class="share-cell">${share.toFixed(1)}%<svg class="share-bar" viewBox="0 0 100 4" aria-hidden="true"><rect class="share-background" width="100" height="4" rx="2"/><rect class="share-fill" width="${Math.min(100,share).toFixed(2)}" height="4" rx="2"/></svg></span></div>`;
}
const iconQueue=[];let activeIconLoads=0;
const iconObserver=new IntersectionObserver(entries=>{entries.forEach(entry=>{if(entry.isIntersecting){iconObserver.unobserve(entry.target);iconQueue.push(entry.target);}});pumpIcons();},{rootMargin:'80px 0px'});
function observeApplicationIcons(){iconObserver.disconnect();iconQueue.splice(0,iconQueue.length);document.querySelectorAll('#traffic-table img[data-icon-id]').forEach(image=>iconObserver.observe(image));}
function pumpIcons(){
  if(document.hidden)return;
  while(activeIconLoads<2&&iconQueue.length){const image=iconQueue.shift();if(!image.isConnected)continue;activeIconLoads++;
    let finished=false;const timeout=setTimeout(()=>finish(false),12000);
    function finish(ok){if(finished)return;finished=true;clearTimeout(timeout);if(ok&&image.isConnected)image.parentElement.classList.add('has-icon');else image.remove();activeIconLoads--;pumpIcons();}
    image.addEventListener('load',()=>finish(image.naturalWidth>0),{once:true});image.addEventListener('error',()=>finish(false),{once:true});image.fetchPriority='low';image.src='/api/icon?id='+encodeURIComponent(image.dataset.iconId);
  }
}
document.addEventListener('visibilitychange',()=>{if(!document.hidden)pumpIcons();});
function renderTable(){
  if(!state.data)return;const rows=state.data.rows,view=state.view,merge=$('service-toggle').checked;
  const roots=groupRows(rows,r=>view==='app'?JSON.stringify([r.app,r.path]):view==='node'?r.node:merge?service(r.host):r.host),sort=$('sort-order').value;
  if(sort==='name')roots.sort((a,b)=>(view==='app'?a.rows[0].app:a.key).localeCompare(view==='app'?b.rows[0].app:b.key,'zh-CN'));else if(sort!=='total')roots.sort((a,b)=>b[sort]-a[sort]);
  $('record-count').textContent=roots.length;$('dimension-heading').textContent=view==='app'?'应用 / 网站 / 节点':view==='node'?'节点 / 应用 / 网站':'网站 / 应用 / 节点';
  if(!rows.length){$('traffic-table').innerHTML='<div class="empty-state"><span class="empty-icon">↗</span><strong>这里还没有记录</strong><p>记录从应用首次运行开始。你可以正常使用网络后刷新，或调整日期与筛选条件。Clash 外流量需要先在设置中启用 Windows 补充采集。</p></div>';$('table-description').textContent='首次启动之前的历史无法补回。';$('show-more').hidden=true;observeApplicationIcons();return;}
  let html='';roots.slice(0,state.limit).forEach((g,index)=>{
    const first=g.rows[0],key=`${view}:${g.key}`,title=view==='app'?first.app:g.key;const nodes=new Set(g.rows.map(r=>r.node));const subtitle=view==='app'?(first.path||`${nodes.size} 个出口 · ${new Set(g.rows.map(r=>r.host)).size} 个域名`):view==='node'?`${new Set(g.rows.map(r=>r.app)).size} 个应用 · ${new Set(g.rows.map(r=>r.host)).size} 个域名`:`${new Set(g.rows.map(r=>r.app)).size} 个应用 · ${nodes.size} 个出口`;
    html+=rowHtml(g,key,title,subtitle,0,view==='node'?'node':view==='host'?'domain':first.app.toLowerCase().includes('chrome')?'chrome':'',true,'',view==='app'&&first.path?first.id:'');
    if(!state.expanded.has(key))return;
    const children=groupRows(g.rows,r=>view==='app'?(merge?service(r.host):r.host):JSON.stringify([r.app,r.path]));
    children.forEach(child=>{const r=child.rows[0],childKey=key+'::'+child.key,childTitle=view==='app'?child.key:r.app,childSub=view==='app'?`${new Set(child.rows.map(v=>v.host)).size} 个域名 · ${new Set(child.rows.map(v=>v.node)).size} 个出口`:(r.path||'展开查看域名和节点');
      html+=rowHtml(child,childKey,childTitle,childSub,1,view==='app'?'domain':'',true,'',view!=='app'&&r.path?r.id:'');
      if(!state.expanded.has(childKey))return;
      groupRows(child.rows,r=>JSON.stringify([r.host,r.node,r.source,r.route])).forEach(leaf=>{const l=leaf.rows[0];html+=rowHtml(leaf,'',l.host,l.node+(l.source==='windows'?' · 无法从加密连接确定域名':` · ${l.network.toUpperCase()}`),2,'',false,routeTag(l));});
    });
  });$('traffic-table').innerHTML=html;observeApplicationIcons();$('table-description').textContent=`${roots.length} 个${view==='app'?'应用':view==='node'?'出口':'网站'} · ${rows.length.toLocaleString()} 组明细${state.data.truncated?'（展示最高流量组合）':''}`;$('show-more').hidden=roots.length<=state.limit;
}
function renderHistory(){const events=state.data.events;if(!events.length){$('history-list').innerHTML='<p>这段时间没有状态事件。只展示已记录的启动、中断与恢复。</p>';return;}const render=e=>`<div class="history-item ${e.kind.includes('gap')?'warn':''}"><time>${esc(stamp(Number(e.at),true))}</time><span class="history-body">${esc(e.detail)}</span></div>`;$('history-list').innerHTML=events.slice(0,4).map(render).join('')+(events.length>4?`<details><summary>查看其余 ${events.length-4} 条状态</summary>${events.slice(4).map(render).join('')}</details>`:'');}
function resetView(){state.expanded.clear();state.limit=30;}
$('refresh-button').addEventListener('click',refresh);
$('presets').addEventListener('click',e=>{const b=e.target.closest('[data-days]');if(b){preset(Number(b.dataset.days));resetView();refresh();}});
['date-from','date-to'].forEach(id=>$(id).addEventListener('change',()=>{pressed('#presets',()=>false);resetView();refresh();}));
$('scopes').addEventListener('click',e=>{const b=e.target.closest('[data-scope]');if(!b)return;state.scope=b.dataset.scope;pressed('#scopes',x=>x===b);resetView();refresh();});
$('japan-filter').addEventListener('click',()=>{state.japan=!state.japan;$('japan-filter').classList.toggle('active',state.japan);$('japan-filter').setAttribute('aria-pressed',String(state.japan));resetView();refresh();});
$('node-filter').addEventListener('change',()=>{resetView();refresh();});
let searchTimer;$('search').addEventListener('input',()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>{resetView();refresh();},350);});
$('views').addEventListener('click',e=>{const b=e.target.closest('[data-view]');if(!b)return;state.view=b.dataset.view;pressed('#views',x=>x===b);resetView();renderTable();});
$('sort-order').addEventListener('change',()=>{state.limit=30;renderTable();});
$('service-toggle').addEventListener('change',()=>{resetView();renderTable();});
function expandRow(e){const r=e.target.closest('[data-expand]');if(!r)return;const key=r.dataset.expand;if(state.expanded.has(key))state.expanded.delete(key);else state.expanded.add(key);renderTable();}
$('traffic-table').addEventListener('click',expandRow);$('traffic-table').addEventListener('keydown',e=>{if((e.key==='Enter'||e.key===' ')&&e.target.hasAttribute('data-expand')){e.preventDefault();expandRow(e);}});
$('show-more').addEventListener('click',()=>{state.limit+=30;renderTable();});
function settingsVisible(show){$('settings-panel').hidden=!show;$('settings-button').setAttribute('aria-expanded',String(show));}
$('settings-button').addEventListener('click',()=>settingsVisible($('settings-panel').hidden));$('close-settings').addEventListener('click',()=>{settingsVisible(false);$('settings-button').focus();});
window.addEventListener('hashchange',()=>{if(location.hash==='#settings'){settingsVisible(true);$('settings-panel').scrollIntoView();}});if(location.hash==='#settings')settingsVisible(true);
document.querySelectorAll('.main-nav a').forEach(a=>a.addEventListener('click',()=>document.querySelectorAll('.main-nav a').forEach(b=>b.classList.toggle('active',a===b))));
$('accuracy-toggle').addEventListener('click',()=>{const show=$('accuracy-detail').hidden;$('accuracy-detail').hidden=!show;$('accuracy-toggle').setAttribute('aria-expanded',String(show));});
$('startup-toggle').addEventListener('change',async()=>{const b=$('startup-toggle');b.disabled=true;try{const r=await api('/api/startup?enabled='+b.checked,true);b.checked=r.enabled;toast(r.enabled?'已开启登录后自启动':'已关闭自启动');refresh();}catch(error){b.checked=!b.checked;toast(error.message);}finally{b.disabled=false;}});
$('repair-startup').addEventListener('click',async()=>{const b=$('repair-startup');b.disabled=true;try{await api('/api/startup?enabled=true',true);toast('自启动已关联到当前目录');refresh();}catch(error){toast(error.message);}finally{b.disabled=false;}});
$('enable-windows').addEventListener('click',async()=>{const b=$('enable-windows');b.disabled=true;try{const r=await api('/api/enable-windows',true);toast(r.accepted?'请在 Windows 授权提示中确认。授权完成后刷新查看状态。':r.message);setTimeout(refresh,8000);}catch(error){toast(error.message);}finally{b.disabled=false;}});
const systemTheme=matchMedia('(prefers-color-scheme: dark)');
function applyTheme(){const dark=state.theme==='dark'||(state.theme==='system'&&systemTheme.matches);document.documentElement.dataset.theme=dark?'dark':'light';$('theme-select').value=state.theme;$('theme-button').setAttribute('aria-label',dark?'切换到浅色主题':'切换到深色主题');$('theme-button').title=dark?'切换到浅色主题':'切换到深色主题';}
systemTheme.addEventListener('change',applyTheme);applyTheme();
async function savePreferences(theme,auto){const controls=[$('theme-button'),$('theme-select'),$('auto-refresh-toggle')];controls.forEach(b=>b.disabled=true);try{const r=await api('/api/preferences?'+new URLSearchParams({theme,autoRefresh:String(auto)}),true);state.theme=r.theme;state.auto=r.autoRefresh;toast('偏好已保存');}catch(error){toast(error.message);}finally{applyTheme();$('auto-refresh-toggle').checked=state.auto;controls.forEach(b=>b.disabled=false);}}
$('theme-button').addEventListener('click',()=>savePreferences(document.documentElement.dataset.theme==='dark'?'light':'dark',state.auto));$('theme-select').addEventListener('change',()=>savePreferences($('theme-select').value,state.auto));$('auto-refresh-toggle').addEventListener('change',()=>savePreferences(state.theme,$('auto-refresh-toggle').checked));
function updatePortField(){$('port-number').hidden=$('port-mode').value!=='fixed';}
$('port-mode').addEventListener('change',updatePortField);$('save-port').addEventListener('click',async()=>{const b=$('save-port'),port=$('port-mode').value==='auto'?0:Number($('port-number').value);if(port!==0&&(!Number.isInteger(port)||port<1024||port>65535)){toast('请输入 1024–65535 的整数端口号');return;}if($('port-mode').value==='fixed'&&port===0){toast('请输入指定端口号');return;}b.disabled=true;try{const r=await api('/api/port?port='+port,true);state.portLoaded=false;toast(r.restartRequired?'已保存，退出托盘后重新启动生效':'端口设置已保存');refresh();}catch(error){toast(error.message);}finally{b.disabled=false;}});
$('reset-filters').addEventListener('click',()=>{clearTimeout(searchTimer);state.scope='all';state.japan=false;$('search').value='';$('node-filter').value='';pressed('#scopes',b=>b.dataset.scope==='all');$('japan-filter').classList.remove('active');$('japan-filter').setAttribute('aria-pressed','false');resetView();refresh();});
setInterval(()=>{if(state.auto&&!document.hidden&&!state.loading)refresh();},15000);
$('export-button').addEventListener('click',()=>{window.location.href='/api/export?'+params();});
preset(1);refresh();
let resizeTimer;window.addEventListener('resize',()=>{clearTimeout(resizeTimer);resizeTimer=setTimeout(()=>{if(state.data)renderChart();},100);});
