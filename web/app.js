'use strict';
const $=id=>document.getElementById(id);
const state={scope:'all',view:'app',japan:false,data:null,status:null,expanded:new Set(),limit:30,loading:false,request:0,csrf:'',auto:false,theme:'system',preferencesLoaded:false,portLoaded:false,range:null,chartDates:null,pendingStart:null,chartGeometry:null};
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function bytes(n,markup=false){n=Number(n)||0;const units=['B','KB','MB','GB','TB'];let i=0;while(n>=1024&&i<units.length-1){n/=1024;i++;}const value=n.toLocaleString('en-US',{maximumFractionDigits:i===0?0:2,minimumFractionDigits:i>=3?2:0});return markup?`${value}<small>${units[i]}</small>`:`${value} ${units[i]}`;}
const stamp=(seconds,day=false)=>new Intl.DateTimeFormat('zh-CN',{timeZone:'Asia/Shanghai',...(day?{month:'2-digit',day:'2-digit'}:{}),hour:'2-digit',minute:'2-digit',second:day?undefined:'2-digit',hour12:false}).format(new Date(seconds*1000));
const dateOf=d=>new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit'}).format(d);
function pressed(group,match){document.querySelectorAll(group+' button').forEach(b=>{const active=match(b);b.classList.toggle('active',active);b.setAttribute('aria-pressed',String(active));});}
function makeRange(from,to,fromTime='',toTime=''){
  const start=Date.parse(from+'T'+(fromTime||'00:00')+':00+08:00')/1000,end=Date.parse(to+'T'+(toTime||'23:59')+':00+08:00')/1000+60;
  if(!Number.isFinite(start)||!Number.isFinite(end)||end<=start||end-start>3660*86400)throw new Error('请选择有效的时间范围，起点应不晚于终点。');
  return {from,to,fromTime,toTime,start,end};
}
const timeOf=seconds=>new Date((seconds+8*3600)*1000).toISOString().slice(11,16);
const fullStamp=seconds=>dateOf(new Date(seconds*1000))+' '+timeOf(seconds);
function timeFiltered(){if(!state.range||!state.chartDates)return false;const full=makeRange(state.chartDates.from,state.chartDates.to);return state.range.start!==full.start||state.range.end!==full.end;}
function syncRangeInputs(){const r=state.range;$('date-from').value=r.from;$('date-to').value=r.to;$('time-from').value=r.fromTime||'00:00';$('time-to').value=r.toTime||'23:59';$('range-whole-days').checked=!r.fromTime&&!r.toTime;rangeTimeFields();$('range-error').hidden=true;}
function rangeTimeFields(){const whole=$('range-whole-days').checked;document.querySelectorAll('.time-field').forEach(label=>label.hidden=whole);$('time-from').disabled=whole;$('time-to').disabled=whole;$('range-help').textContent=whole?'包含开始与结束日期的全天记录。':'精确到分钟，包含结束分钟的全部记录。';}
function updateRangeLabels(){const r=state.range;$('range-from-label').textContent=r.from+(r.fromTime?' '+r.fromTime:'');$('range-to-label').textContent=r.to+(r.toTime?' '+r.toTime:'');$('range-picker-button').setAttribute('aria-label','选择时间范围：'+fullStamp(r.start)+' 至 '+fullStamp(r.end-60)+'，北京时间');updateSelectionHint();}
function useRange(range,resetChart=true){state.range=range;if(resetChart){state.chartDates={from:range.from,to:range.to};state.chartPreset=null;}state.pendingStart=null;syncRangeInputs();updateRangeLabels();}
function preset(days){let end=new Date(dateOf(new Date())+'T00:00:00+08:00'),start=new Date(end);if(days===2){start.setUTCDate(start.getUTCDate()-1);end=new Date(start);}else start.setUTCDate(start.getUTCDate()-days+1);useRange(makeRange(dateOf(start),dateOf(end)));state.chartPreset=days;pressed('#presets',b=>Number(b.dataset.days)===days);}
function params(){const r=state.range,q=new URLSearchParams({from:r.from,to:r.to,chartFrom:state.chartDates.from,chartTo:state.chartDates.to,scope:state.scope,search:$('search').value.trim()});if(r.fromTime)q.set('fromTime',r.fromTime);if(r.toTime)q.set('toTime',r.toTime);if(state.japan)q.set('japan','true');if($('node-filter').value)q.set('node',$('node-filter').value);return q;}
function rangePickerVisible(show,focus=true){if(show){syncRangeInputs();$('range-picker').hidden=false;if(focus)$('date-from').focus();}else $('range-picker').hidden=true;$('range-picker-button').setAttribute('aria-expanded',String(show));}
$('range-picker-button').addEventListener('click',()=>rangePickerVisible($('range-picker').hidden));
$('close-range-picker').addEventListener('click',()=>{rangePickerVisible(false);$('range-picker-button').focus();});
$('range-whole-days').addEventListener('change',rangeTimeFields);
$('apply-range').addEventListener('click',()=>{try{const whole=$('range-whole-days').checked;for(const input of [$('date-from'),$('date-to'),...(!whole?[$('time-from'),$('time-to')]:[])])if(!input.reportValidity())return;const range=makeRange($('date-from').value,$('date-to').value,whole?'':$('time-from').value,whole?'':$('time-to').value);useRange(range);pressed('#presets',()=>false);rangePickerVisible(false);$('range-picker-button').focus();resetView();refresh();}catch(error){$('range-error').textContent=error.message;$('range-error').hidden=false;}});
document.addEventListener('pointerdown',e=>{if(!$('range-picker').hidden&&!e.target.closest('.date-range'))rangePickerVisible(false,false);});
document.addEventListener('keydown',e=>{if(e.key!=='Escape')return;if(!$('range-picker').hidden){rangePickerVisible(false);$('range-picker-button').focus();}else if(state.pendingStart!==null){state.pendingStart=null;drawChartSelection();}});
$('range-picker').addEventListener('keydown',e=>{if(e.key==='Tab'){const controls=[...$('range-picker').querySelectorAll('button,input')].filter(el=>!el.disabled&&!el.closest('[hidden]'));if(e.shiftKey&&document.activeElement===controls[0]){e.preventDefault();controls.at(-1).focus();}else if(!e.shiftKey&&document.activeElement===controls.at(-1)){e.preventDefault();controls[0].focus();}}});
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
  $('accuracy-text').textContent=`所选时间内，全 Clash 内核累计 ${bytes(core)} · 未归属差额参考 ${bytes(missing)}。各来源按各自口径计量。`;
  const selected=$('node-filter').value,nodeNames=selected&&!d.nodes.includes(selected)?[selected,...d.nodes]:d.nodes;$('node-filter').innerHTML='<option value="">全部节点</option>'+nodeNames.map(n=>`<option value="${esc(n)}">${esc(n)}</option>`).join('');$('node-filter').value=selected;
  $('range-summary').textContent=fullStamp(state.range.start)+' — '+fullStamp(state.range.end-60)+' · 北京时间（含结束分钟）';
  $('inventory-summary').textContent=`${Number(s.applications)||0} 个应用 · ${Number(s.hosts)||0} 个域名 / IP`;
  const filters=[];if(timeFiltered())filters.push('时间：'+fullStamp(state.range.start)+' — '+fullStamp(state.range.end-60));if(state.scope!=='all')filters.push(({proxy:'代理',direct:'Clash 直连',windows:'Clash 外'})[state.scope]);if(state.japan)filters.push('日本节点');if(selected)filters.push(selected);if($('search').value.trim())filters.push('搜索：'+$('search').value.trim());
  $('filter-summary').hidden=!filters.length;$('active-filters').textContent=filters.join(' · ');
  renderChart();renderComposition(proxy,direct,windows,total);renderTable();renderHistory();
}
function renderChart(){
  const d=state.data,raw=d.trend,step=Number(d.trendStep);$('trend-subtitle').textContent=`按 ${step>=3600?'小时':step/60+' 分钟'}汇总 · 点击图表选择时间范围`;
  const width=Math.max(280,$('trend-chart').clientWidth-30),height=225,left=58,right=12,top=20,bottom=30,plotH=height-top-bottom,plotW=width-left-right;
  const from=Number(d.trendFrom??d.from),to=Number(d.trendTo??d.to),peak=raw.reduce((n,r)=>Math.max(n,Number(r.upload),Number(r.download)),1);
  const scale=10**Math.floor(Math.log10(peak)),max=Math.ceil(peak/scale)*scale;
  const x=t=>left+(t-from)/(to-from)*plotW,y=n=>top+plotH-(n/max)*plotH;
  state.chartGeometry={width,height,left,right,top,bottom,plotW,plotH,from,to};
  let svg=`<svg viewBox="0 0 ${width} ${height}" role="group" aria-label="上下行用量趋势。点击选起点，再次点击选终点。Tab 进入数据点，左右方向键切换，回车选取。缺失时段不连线。"><defs><linearGradient id="download-area" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stop-color="var(--blue)" stop-opacity=".18"/><stop offset="100%" stop-color="var(--blue)" stop-opacity=".01"/></linearGradient></defs>`;
  for(let i=0;i<4;i++){const n=max*(1-i/3),yy=y(n);svg+=`<line class="chart-grid" x1="${left}" x2="${width-right}" y1="${yy}" y2="${yy}"/><text class="chart-text" x="${left-9}" y="${yy+3}" text-anchor="end">${bytes(n)}</text>`;}
  ['download','upload'].forEach(field=>{const segments=[];let previous=-Infinity;raw.forEach(r=>{const t=Number(r.bucket);if(t-previous>step*1.5)segments.push([]);segments.at(-1).push([x(t),y(Number(r[field]))]);previous=t;});segments.forEach(points=>{const path=points.map((p,i)=>(i?'L':'M')+p.map(n=>n.toFixed(1)).join(',')).join(' ');if(field==='download')svg+=`<path fill="url(#download-area)" d="${path} L${points.at(-1)[0]},${y(0)} L${points[0][0]},${y(0)} Z"/>`;svg+=`<path class="chart-line ${field}-line" d="${path}"/>`;});});
  svg+=`<rect class="chart-select-surface" data-chart-surface="true" x="${left}" y="${top}" width="${plotW}" height="${plotH}" aria-hidden="true"/>`;
  raw.forEach((r,i)=>{const xx=x(Number(r.bucket)),yy=y(Number(r.download)),label=`${stamp(Number(r.bucket),true)}，下载 ${bytes(r.download)}，上传 ${bytes(r.upload)}。回车选择此时间。`;svg+=`<circle cx="${xx.toFixed(1)}" cy="${yy.toFixed(1)}" r="2" fill="var(--blue)" pointer-events="none"/><rect class="chart-focus" data-point="${i}" data-x="${xx}" data-width="${width}" x="${Math.max(left,xx-plotW*step/(to-from)/2)}" y="${top}" width="${Math.max(2,Math.min(width-right-xx,plotW*step/(to-from)))}" height="${plotH}" tabindex="${i===0?0:-1}" role="button" aria-label="${esc(label)}" fill-opacity=".08"><title>${esc(label)}</title></rect>`;});
  for(let i=0;i<5;i++){const t=from+(to-from)*i/4;const label=to-from>86400?new Intl.DateTimeFormat('zh-CN',{timeZone:'Asia/Shanghai',month:'2-digit',day:'2-digit'}).format(new Date((t-(i===4?1:0))*1000)):stamp(t-(i===4?1:0)).slice(0,5);svg+=`<text class="chart-text" x="${x(t)}" y="${height-5}" text-anchor="${i===0?'start':i===4?'end':'middle'}">${esc(label)}</text>`;}
  svg+=`<g pointer-events="none"><rect id="chart-range-fill" class="chart-range-fill" y="${top}" height="${plotH}" hidden/><line id="chart-range-start" class="chart-range-line" y1="${top}" y2="${top+plotH}" hidden/><line id="chart-range-end" class="chart-range-line" y1="${top}" y2="${top+plotH}" hidden/><line id="chart-pending-line" class="chart-pending-line" y1="${top}" y2="${top+plotH}" hidden/></g>`;
  svg+='<g id="chart-tooltip" class="chart-tooltip" hidden pointer-events="none"><rect width="164" height="58" rx="7"/><text x="11" y="17" id="tooltip-time"></text><text x="11" y="33" id="tooltip-download"></text><text x="11" y="48" id="tooltip-upload"></text></g>';
  $('trend-chart').innerHTML=svg+'</svg>'+(!raw.length?'<div class="chart-empty-overlay">这段时间没有流量记录<br><small>仍可点击选取时间范围</small></div>':'');
  const largest=raw.length?raw.reduce((a,b)=>Number(a.upload)+Number(a.download)>Number(b.upload)+Number(b.download)?a:b):null;$('trend-peak').textContent=largest?`单时段峰值 ${bytes(Number(largest.upload)+Number(largest.download))} · ${stamp(Number(largest.bucket))}`:'暂无峰值记录';drawChartSelection();
}
function updateSelectionHint(){const filtered=timeFiltered();$('chart-selection-hint').textContent=state.pendingStart!==null?'起点 '+fullStamp(state.pendingStart)+' · 再次点击选终点，Esc 取消起点':filtered?'已选 '+fullStamp(state.range.start)+' — '+fullStamp(state.range.end-60)+' · 再点开始新选区':'点击选起点，再次点击选终点。';$('clear-time-range').hidden=!filtered&&state.pendingStart===null;$('clear-time-range').textContent=state.pendingStart!==null&&!filtered?'取消起点 ×':'恢复整天 ×';}
function drawChartSelection(){
  updateSelectionHint();const g=state.chartGeometry;if(!g||!$('chart-range-fill'))return;const x=t=>g.left+(Math.max(g.from,Math.min(g.to,t))-g.from)/(g.to-g.from)*g.plotW,filtered=timeFiltered();
  ['chart-range-fill','chart-range-start','chart-range-end'].forEach(id=>$(id).toggleAttribute('hidden',!filtered));
  if(filtered){const start=x(state.range.start),end=x(state.range.end);$('chart-range-fill').setAttribute('x',start);$('chart-range-fill').setAttribute('width',Math.max(1,end-start));for(const [id,xx] of [['chart-range-start',start],['chart-range-end',end]]){$(id).setAttribute('x1',xx);$(id).setAttribute('x2',xx);}}
  $('chart-pending-line').toggleAttribute('hidden',state.pendingStart===null);if(state.pendingStart!==null){$('chart-pending-line').setAttribute('x1',x(state.pendingStart));$('chart-pending-line').setAttribute('x2',x(state.pendingStart));}
}
function selectChartTime(seconds){const g=state.chartGeometry;if(!g)return;const point=Math.max(g.from,Math.min(g.to-60,Math.floor(seconds/60)*60));if(state.pendingStart===null){state.pendingStart=point;drawChartSelection();return;}const first=Math.min(point,state.pendingStart),last=Math.max(point,state.pendingStart);useRange(makeRange(dateOf(new Date(first*1000)),dateOf(new Date(last*1000)),timeOf(first),timeOf(last)),false);pressed('#presets',()=>false);drawChartSelection();resetView();refresh();}
$('trend-chart').addEventListener('click',e=>{const g=state.chartGeometry,svg=$('trend-chart').querySelector('svg');if(!g||!svg)return;const point=e.target.closest('[data-point]');if(point){selectChartTime(Number(state.data.trend[Number(point.dataset.point)].bucket));return;}const matrix=svg.getScreenCTM();if(!matrix)return;const p=new DOMPoint(e.clientX,e.clientY).matrixTransform(matrix.inverse());if(p.x<g.left||p.x>g.left+g.plotW||p.y<g.top||p.y>g.top+g.plotH)return;selectChartTime(Math.round((g.from+(p.x-g.left)/g.plotW*(g.to-g.from))/60)*60);});
function clearTimeRange(){useRange(makeRange(state.chartDates.from,state.chartDates.to),false);pressed('#presets',b=>Number(b.dataset.days)===state.chartPreset);drawChartSelection();resetView();refresh();}
$('clear-time-range').addEventListener('click',()=>{if(state.pendingStart!==null&&!timeFiltered()){state.pendingStart=null;drawChartSelection();}else clearTimeRange();});
function chartTooltip(target){const point=target.closest('[data-point]');if(!point){hideChartTooltip();return;}const r=state.data.trend[Number(point.dataset.point)],tip=$('chart-tooltip');tip.removeAttribute('hidden');tip.setAttribute('transform',`translate(${Math.max(5,Math.min(Number(point.dataset.width)-169,Number(point.dataset.x)-82))},2)`);$('tooltip-time').textContent=stamp(Number(r.bucket),true);$('tooltip-download').textContent='↓ 下载 '+bytes(r.download);$('tooltip-upload').textContent='↑ 上传 '+bytes(r.upload);}
$('trend-chart').addEventListener('pointerover',e=>chartTooltip(e.target));$('trend-chart').addEventListener('focusin',e=>chartTooltip(e.target));
function hideChartTooltip(){if($('chart-tooltip'))$('chart-tooltip').setAttribute('hidden','');}
$('trend-chart').addEventListener('pointerleave',hideChartTooltip);$('trend-chart').addEventListener('focusout',hideChartTooltip);
$('trend-chart').addEventListener('keydown',e=>{if(e.key==='Escape'){hideChartTooltip();state.pendingStart=null;drawChartSelection();}if(!e.target.hasAttribute('data-point'))return;if(e.key==='Enter'||e.key===' '){e.preventDefault();selectChartTime(Number(state.data.trend[Number(e.target.dataset.point)].bucket));return;}if(!['ArrowLeft','ArrowRight','Home','End'].includes(e.key))return;e.preventDefault();const points=[...$('trend-chart').querySelectorAll('[data-point]')],i=Number(e.target.dataset.point),next=e.key==='Home'?0:e.key==='End'?points.length-1:Math.max(0,Math.min(points.length-1,i+(e.key==='ArrowLeft'?-1:1)));e.target.setAttribute('tabindex','-1');points[next].setAttribute('tabindex','0');points[next].focus();});
function renderComposition(proxy,direct,windows,total){
  const sources=[{name:'通过代理',n:proxy,color:'blue'},{name:'Clash 直连',n:direct,color:'teal'},{name:'Clash 外',n:windows,color:'amber'}];if(total-proxy-direct-windows>0)sources.push({name:'其他路线',n:total-proxy-direct-windows,color:'muted'});
  let offset=0;const segments=sources.map(s=>{const percentage=total?s.n/total*100:0;const segment=`<circle class="segment" cx="60" cy="60" r="46" pathLength="100" stroke="var(--${s.color})" stroke-dasharray="${percentage} ${100-percentage}" stroke-dashoffset="${-offset}"/>`;offset+=percentage;return segment;}).join('');
  $('source-chart').innerHTML=`<svg viewBox="0 0 120 120" role="img" aria-label="流量来源占比"><circle class="source-track" cx="60" cy="60" r="46" stroke-width="10"/>${segments}</svg><div class="source-center">${total?(proxy/total*100).toFixed(0)+'%':'—'}<small>代理占比</small></div>`;
  $('source-legend').innerHTML=sources.map(s=>`<div class="source-item"><span><i class="dot ${s.color}"></i>${s.name}</span><span>${total?(s.n/total*100).toFixed(1):'0.0'}%</span></div>`).join('');
  const sites=groupRows(state.data.rows,r=>service(r.host));$('sites-caption').textContent=state.data.truncated?'前 5,000 组内的排行':'按用量排序';$('top-sites').innerHTML=sites.length?sites.slice(0,3).map((s,i)=>`<div class="top-site"><span class="site-rank">0${i+1}</span><span class="site-name" title="${esc(s.key)}">${esc(s.key||'未知网站')}</span><span class="site-amount">${bytes(s.upload+s.download)}</span></div>`).join(''):'<p class="sites-empty">有记录后，主要流量去向会出现在这里。</p>';
}
function service(host){host=host.toLowerCase();const match=s=>host===s||host.endsWith('.'+s);if(['youtube.com','youtu.be','googlevideo.com','ytimg.com','youtube-nocookie.com'].some(match))return 'YouTube';if(['chatgpt.com','openai.com','oaistatic.com','oaiusercontent.com'].some(match))return 'ChatGPT / OpenAI';if(['github.com','githubassets.com','githubusercontent.com'].some(match))return 'GitHub';if(['claude.ai','anthropic.com'].some(match))return 'Claude / Anthropic';return host;}
function groupRows(rows,keyFn){const groups=new Map();rows.forEach(r=>{const key=keyFn(r);if(!groups.has(key))groups.set(key,{key,rows:[],upload:0,download:0});const g=groups.get(key);g.rows.push(r);g.upload+=Number(r.upload);g.download+=Number(r.download);});return [...groups.values()].sort((a,b)=>(b.upload+b.download)-(a.upload+a.download));}
function sortChoice(){const value=$('sort-order').value;return {field:value.replace(/-asc$/,''),ascending:value==='name'||value.endsWith('-asc')};}
function sortTableGroups(groups,title){const sort=sortChoice(),name=(a,b)=>title(a).localeCompare(title(b),'zh-CN')||a.key.localeCompare(b.key,'zh-CN'),value=g=>sort.field==='upload'?g.upload:sort.field==='download'?g.download:g.upload+g.download;return groups.sort((a,b)=>sort.field==='name'?name(a,b):(sort.ascending?value(a)-value(b):value(b)-value(a))||name(a,b));}
function renderSortHeaders(){const sort=sortChoice();document.querySelectorAll('[data-sort-field]').forEach(button=>{const active=button.dataset.sortField===sort.field,label=button.dataset.sortLabel;button.setAttribute('aria-pressed',String(active));button.querySelector('.sort-arrow').textContent=active?(sort.ascending?'↑':'↓'):'↕';button.setAttribute('aria-label',label+(active?'，当前'+(sort.ascending?'从小到大':'从大到小'):'')+'，点击'+(active&&state.sortTouched&&!sort.ascending?'从小到大':'从大到小')+'排序');});}
function routeInfo(r){return r.source==='windows'?{key:'windows',label:'Clash 外',className:'windows',order:2}:r.route==='DIRECT'?{key:'direct',label:'直连',className:'direct',order:1}:r.route==='PROXY'?{key:'proxy',label:'代理',className:'proxy',order:0}:r.route==='REJECT'?{key:'reject',label:'拒绝',className:'other',order:3}:{key:'other',label:'其他路线',className:'other',order:4};}
function routeTag(r){const tag=routeInfo(r);return `<span class="route-tag ${tag.className}">${tag.label}</span>`;}
function routeTags(rows){const tags=new Map();rows.forEach(r=>{const info=routeInfo(r);if(!tags.has(info.key))tags.set(info.key,{info,row:r});});return [...tags.values()].sort((a,b)=>a.info.order-b.info.order).map(t=>routeTag(t.row)).join('');}
function proxyNodes(rows){return [...new Set(rows.filter(r=>r.source==='mihomo'&&r.route==='PROXY').map(r=>r.node).filter(Boolean))].sort((a,b)=>a.localeCompare(b,'zh-CN'));}
function rowHtml(g,key,title,subtitle,level,icon,expandable,tag='',applicationId='',routeRows=null){
  const total=Number(state.data.summary.upload)+Number(state.data.summary.download),share=total?(g.upload+g.download)/total*100:0,open=state.expanded.has(key);
  const nodes=level<2&&!(level===0&&state.view==='node')?proxyNodes(routeRows||g.rows):[];
  const nodeLabels=nodes.length?`<div class="row-nodes" aria-label="实际代理节点">${nodes.map(node=>`<span class="node-tag" title="${esc(node)}"><span aria-hidden="true">↗</span> ${esc(node)}</span>`).join('')}</div>`:'';
  return `<div class="traffic-row ${level===1?'child':level===2?'grandchild':''} ${expandable?'expandable':''} ${open?'expanded':''}" ${expandable?`data-expand="${esc(key)}" role="button" tabindex="0" aria-expanded="${open}"`:''}><div class="row-identity">${expandable?`<span class="chevron" aria-hidden="true">${open?'⌄':'›'}</span>`:''}${level===2?'':`<span class="app-icon ${icon}"><span class="app-icon-fallback" aria-hidden="true">${esc(icon==='node'?'↗':icon==='domain'?'◈':title.slice(0,1).toUpperCase())}</span>${applicationId?`<img data-icon-id="${esc(applicationId)}" alt="" width="48" height="48" decoding="async">`:''}</span>`}<div class="identity-text"><div class="row-title" title="${esc(title)}"><span class="row-name">${esc(title)}</span>${tag?`<span class="row-tags" aria-label="当前筛选内的流量路径">${tag}</span>`:''}</div><div class="row-subtitle" title="${esc(subtitle)}">${esc(subtitle)}</div>${nodeLabels}</div></div><span class="number total">${bytes(g.upload+g.download)}</span><span class="number">${bytes(g.upload)}</span><span class="number">${bytes(g.download)}</span><span class="share-cell">${share.toFixed(1)}%<svg class="share-bar" viewBox="0 0 100 4" aria-hidden="true"><rect class="share-background" width="100" height="4" rx="2"/><rect class="share-fill" width="${Math.min(100,share).toFixed(2)}" height="4" rx="2"/></svg></span></div>`;
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
  const rootKey=r=>view==='app'?JSON.stringify([r.app,r.path]):view==='node'?r.node:merge?service(r.host):r.host;
  const childKeyOf=r=>view==='app'?(merge?service(r.host):r.host):JSON.stringify([r.app,r.path]);
  const routes=new Map(),childRoutes=new Map();
  (state.data.applicationRoutes||rows).forEach(r=>{const root=rootKey(r),child=JSON.stringify([root,childKeyOf(r)]);if(!routes.has(root))routes.set(root,[]);routes.get(root).push(r);if(!childRoutes.has(child))childRoutes.set(child,[]);childRoutes.get(child).push(r);});
  const roots=sortTableGroups(groupRows(rows,rootKey),g=>view==='app'?g.rows[0].app:g.key);
  renderSortHeaders();
  $('record-count').textContent=roots.length;$('dimension-heading').textContent=view==='app'?'应用 / 网站 / 节点':view==='node'?'节点 / 应用 / 网站':'网站 / 应用 / 节点';
  if(!rows.length){$('traffic-table').innerHTML='<div class="empty-state"><span class="empty-icon">↗</span><strong>这里还没有记录</strong><p>记录从应用首次运行开始。你可以正常使用网络后刷新，或调整日期与筛选条件。Clash 外流量需要先在设置中启用 Windows 补充采集。</p></div>';$('table-description').textContent='首次启动之前的历史无法补回。';$('show-more').hidden=true;observeApplicationIcons();return;}
  let html='';roots.slice(0,state.limit).forEach((g,index)=>{
    const first=g.rows[0],key=`${view}:${g.key}`,title=view==='app'?first.app:g.key;const nodes=new Set(g.rows.map(r=>r.node));const subtitle=view==='app'?(first.path||`${nodes.size} 个出口 · ${new Set(g.rows.map(r=>r.host)).size} 个域名`):view==='node'?`${new Set(g.rows.map(r=>r.app)).size} 个应用 · ${new Set(g.rows.map(r=>r.host)).size} 个域名`:`${new Set(g.rows.map(r=>r.app)).size} 个应用 · ${nodes.size} 个出口`;
    const rootRoutes=routes.get(g.key)||g.rows;
    html+=rowHtml(g,key,title,subtitle,0,view==='node'?'node':view==='host'?'domain':first.app.toLowerCase().includes('chrome')?'chrome':'',true,routeTags(rootRoutes),view==='app'&&first.path?first.id:'',rootRoutes);
    if(!state.expanded.has(key))return;
    const children=sortTableGroups(groupRows(g.rows,childKeyOf),child=>view==='app'?child.key:child.rows[0].app);
    children.forEach(child=>{const r=child.rows[0],childKey=key+'::'+child.key,childTitle=view==='app'?child.key:r.app,childSub=view==='app'?`${new Set(child.rows.map(v=>v.host)).size} 个域名 · ${new Set(child.rows.map(v=>v.node)).size} 个出口`:(r.path||'展开查看域名和节点');
      const branchRoutes=childRoutes.get(JSON.stringify([g.key,child.key]))||child.rows;
      html+=rowHtml(child,childKey,childTitle,childSub,1,view==='app'?'domain':'',true,routeTags(branchRoutes),view!=='app'&&r.path?r.id:'',branchRoutes);
      if(!state.expanded.has(childKey))return;
      sortTableGroups(groupRows(child.rows,r=>JSON.stringify([r.host,r.node,r.source,r.route])),leaf=>leaf.rows[0].host).forEach(leaf=>{const l=leaf.rows[0];html+=rowHtml(leaf,'',l.host,l.node+(l.source==='windows'?' · 无法从加密连接确定域名':` · ${l.network.toUpperCase()}`),2,'',false,routeTag(l));});
    });
  });$('traffic-table').innerHTML=html;observeApplicationIcons();$('table-description').textContent=`${roots.length} 个${view==='app'?'应用':view==='node'?'出口':'网站'} · ${rows.length.toLocaleString()} 组明细${state.data.truncated?'（展示最高流量组合）':''}`;$('show-more').hidden=roots.length<=state.limit;
}
function renderHistory(){const events=state.data.events;if(!events.length){$('history-list').innerHTML='<p>这段时间没有状态事件。只展示已记录的启动、中断与恢复。</p>';return;}const render=e=>`<div class="history-item ${e.kind.includes('gap')?'warn':''}"><time>${esc(stamp(Number(e.at),true))}</time><span class="history-body">${esc(e.detail)}</span></div>`;$('history-list').innerHTML=events.slice(0,4).map(render).join('')+(events.length>4?`<details><summary>查看其余 ${events.length-4} 条状态</summary>${events.slice(4).map(render).join('')}</details>`:'');}
function resetView(){state.expanded.clear();state.limit=30;}
$('refresh-button').addEventListener('click',refresh);
$('presets').addEventListener('click',e=>{const b=e.target.closest('[data-days]');if(b){preset(Number(b.dataset.days));resetView();refresh();}});
$('scopes').addEventListener('click',e=>{const b=e.target.closest('[data-scope]');if(!b)return;state.scope=b.dataset.scope;pressed('#scopes',x=>x===b);resetView();refresh();});
$('japan-filter').addEventListener('click',()=>{state.japan=!state.japan;$('japan-filter').classList.toggle('active',state.japan);$('japan-filter').setAttribute('aria-pressed',String(state.japan));resetView();refresh();});
$('node-filter').addEventListener('change',()=>{resetView();refresh();});
let searchTimer;$('search').addEventListener('input',()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>{resetView();refresh();},350);});
$('views').addEventListener('click',e=>{const b=e.target.closest('[data-view]');if(!b)return;state.view=b.dataset.view;pressed('#views',x=>x===b);resetView();renderTable();});
$('sort-order').addEventListener('change',()=>{state.sortTouched=true;state.limit=30;renderTable();});
document.querySelector('.table-head').addEventListener('click',e=>{const button=e.target.closest('[data-sort-field]');if(!button)return;const field=button.dataset.sortField,current=sortChoice(),ascending=state.sortTouched&&current.field===field&&!current.ascending;$('sort-order').value=field+(ascending?'-asc':'');state.sortTouched=true;state.limit=30;renderTable();});
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
$('reset-filters').addEventListener('click',()=>{clearTimeout(searchTimer);state.scope='all';state.japan=false;$('search').value='';$('node-filter').value='';pressed('#scopes',b=>b.dataset.scope==='all');$('japan-filter').classList.remove('active');$('japan-filter').setAttribute('aria-pressed','false');clearTimeRange();});
setInterval(()=>{if(state.auto&&!document.hidden&&!state.loading)refresh();},15000);
$('export-button').addEventListener('click',()=>{window.location.href='/api/export?'+params();});
preset(1);refresh();
let resizeTimer;window.addEventListener('resize',()=>{clearTimeout(resizeTimer);resizeTimer=setTimeout(()=>{if(state.data)renderChart();},100);});
