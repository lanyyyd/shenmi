/* ==========================================================================
   在线聊天室 · 前端逻辑
   ========================================================================== */

/* ---------------- 主题：按时间自动 + 手动三态 ---------------- */
const LIGHT_FROM = 6, LIGHT_TO = 18;   // 06:00–18:00 浅色（风格A），其余深色（风格B）
const ICONS = {
  auto: '<path d="M12 2a10 10 0 1 0 0 20V2z"/><path d="M12 2a10 10 0 0 1 0 20" opacity=".35"/>',
  light: '<path d="M12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10zm0-5v3m0 14v3M4.2 4.2l2.1 2.1m11.4 11.4l2.1 2.1M2 12h3m14 0h3M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1"/>',
  dark: '<path d="M12.3 3a9 9 0 1 0 8.7 11.4A7.5 7.5 0 0 1 12.3 3z"/>'
};
function resolveTheme(mode) {
  if (mode === 'light') return 'light';
  if (mode === 'dark') return 'dark';
  const h = new Date().getHours();
  return (h >= LIGHT_FROM && h < LIGHT_TO) ? 'light' : 'dark';
}
function applyTheme(mode) {
  const theme = resolveTheme(mode);
  document.documentElement.dataset.mode = mode;
  document.documentElement.dataset.theme = theme;
  try { localStorage.setItem('chat_theme_mode', mode); } catch (e) {}
  const nameEl = document.getElementById('themeName');
  if (!nameEl) return;
  const descEl = document.getElementById('themeDesc'), iconEl = document.getElementById('themeIcon');
  if (mode === 'auto') { nameEl.textContent = '自动'; descEl.textContent = '按时间 · 当前' + (theme === 'light' ? '浅色' : '深色'); }
  else if (mode === 'light') { nameEl.textContent = '浅色'; descEl.textContent = '手动锁定（风格A）'; }
  else { nameEl.textContent = '深色'; descEl.textContent = '手动锁定（风格B）'; }
  iconEl.innerHTML = ICONS[mode];
}
function cycleTheme() {
  const order = ['auto', 'light', 'dark'];
  const cur = document.documentElement.dataset.mode || 'auto';
  const next = order[(order.indexOf(cur) + 1) % order.length];
  applyTheme(next);
  toast({ auto: '🌗 已切回自动模式', light: '☀️ 已锁定浅色模式', dark: '🌙 已锁定深色模式' }[next]);
}
(function initTheme() {
  let saved = 'auto';
  try { saved = localStorage.getItem('chat_theme_mode') || 'auto'; } catch (e) {}
  applyTheme(saved);
  setInterval(() => {
    if ((document.documentElement.dataset.mode || 'auto') === 'auto') {
      const t = resolveTheme('auto');
      if (document.documentElement.dataset.theme !== t) applyTheme('auto');
    }
  }, 60000);
})();

/* ---------------- 状态 ---------------- */
const COLORS = ['#10B981','#3B82F6','#F59E0B','#EF4444','#8B5CF6','#EC4899','#06B6D4','#84CC16','#F97316','#14B8A6'];
const PAGE_SIZE = 80;        // 每页消息数（首屏 + 上滑加载）
let ME = null;
let WS = null;
let onlineUsers = [];
let allUsers = [];          // 全部用户（含离线）
let currentRoom = 'group';
let currentPeer = null;
let unread = {};             // { group: 3, 'pm:1:2': 1 }
let readState = {};          // { userId: { username, lastReadId } } —— 当前房间的已读状态
let oldestId = null, hasMore = false, loadingMore = false, autoStick = true;
let reconnectTimer = null, pingTimer = null;
let mentionGroup = false;                    // 群聊是否有未读的 @我
let mentionPick = new Map();                 // 已选中的 @对象：显示名 -> id | 'all'
let mentionItems = [], mentionIdx = -1;      // @选择框当前候选与高亮下标

function deviceId() {
  try {
    let d = localStorage.getItem('chat_device_id');
    if (!d) { d = 'dev-' + Math.random().toString(36).slice(2, 10) + Date.now().toString(36); localStorage.setItem('chat_device_id', d); }
    return d;
  } catch (e) { return 'dev-' + Math.random().toString(36).slice(2, 10); }
}
const DEVICE = deviceId();
const $ = (id) => document.getElementById(id);
function colorOf(n) { let h = 0; for (let i = 0; i < n.length; i++) h = n.charCodeAt(i) + ((h << 5) - h); return COLORS[Math.abs(h) % COLORS.length]; }
function initial(n) { return (n || '?').slice(-2); }
/* 头像 URL 白名单：仅允许本站 /uploads/ 下的路径，避免注入 */
function avUrlOf(v) { const s = String(v || ''); return /^\/uploads\/[A-Za-z0-9._\/-]+$/.test(s) ? s : ''; }
function avatarHTML(name, avatar, withStatus) {
  const u = avUrlOf(avatar);
  const st = withStatus ? '<span class="status"></span>' : '';
  return u
    ? `<div class="avatar avatar-pic" style="background-image:url('${u}')">${st}</div>`
    : `<div class="avatar" style="background:${colorOf(name || '?')}">${st}${initial(name)}</div>`;
}
function esc(s) { return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c])); }
function dn(u) { return (u && (u.displayName || u.nickname || u.username)) || ''; }
function fmtSize(b) { if (!b) return ''; const u = ['B','KB','MB','GB']; let i = 0, n = b; while (n >= 1024 && i < 3) { n /= 1024; i++; } return n.toFixed(n < 10 && i > 0 ? 1 : 0) + ' ' + u[i]; }
function fmtTime(ts) {
  const d = new Date(ts);
  const hm = d.getHours().toString().padStart(2, '0') + ':' + d.getMinutes().toString().padStart(2, '0');
  const now = new Date();
  const today0 = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const yesterday0 = today0 - 86400000;
  if (ts >= today0) return hm;
  if (ts >= yesterday0) return '昨天' + hm;
  return (d.getMonth() + 1) + '月' + d.getDate() + '日 ' + hm;
}
function pmRoom(a, b) { const x = Math.min(a, b), y = Math.max(a, b); return `pm:${x}:${y}`; }
let tTimer; function toast(m) { const t = $('toast'); t.textContent = m; t.classList.add('on'); clearTimeout(tTimer); tTimer = setTimeout(() => t.classList.remove('on'), 2200); }

/* ---------------- 登录 / 注册 ---------------- */
function switchTab(m) {
  const isReg = m === 'reg';
  document.body.classList.toggle('register', isReg);
  $('tabLogin').classList.toggle('on', !isReg);
  $('tabReg').classList.toggle('on', isReg);
  $('formMsg').textContent = '';
}
function setMsg(text, isErr) { const el = $('formMsg'); el.textContent = text; el.className = 'form-msg ' + (isErr ? 'err' : 'ok'); }

/* ---------------- 邀请码：是否需要 + 是否还有可用码 ---------------- */
let inviteRequired = false;
async function loadInviteInfo() {
  try {
    const r = await fetch('/api/invite-info'); const d = await r.json();
    if (!d.ok) return;
    inviteRequired = !!d.required;
    const wrap = $('regInviteWrap'); if (wrap) wrap.style.display = inviteRequired ? '' : 'none';
    const tip = $('inviteTip');
    if (tip) {
      if (inviteRequired && !d.available) { tip.textContent = '管理员尚未配置可用的邀请码，请联系管理员'; tip.style.display = ''; }
      else tip.style.display = 'none';
    }
  } catch (e) {}
}

async function doAuth() {
  const isReg = document.body.classList.contains('register');
  const username = (isReg ? $('regUser').value : $('loginUser').value).trim();
  const password = isReg ? $('regPass').value : $('loginPass').value;
  const email = isReg ? $('regMail').value.trim() : '';
  const nickname = isReg ? $('regNick').value.trim() : '';
  const inviteCode = isReg ? ($('regInvite').value || '').trim() : '';
  if (!username || !password) return setMsg('请填写用户名和密码', true);
  if (isReg && inviteRequired && !inviteCode) return setMsg('请填写邀请码', true);
  $('authBtn').disabled = true;
  try {
    const r = await fetch(isReg ? '/api/register' : '/api/login', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password, email, nickname, deviceId: DEVICE, inviteCode }),
    });
    const d = await r.json();
    if (!d.ok) { setMsg(d.error || '操作失败', true); return; }
    ME = d.user;
    enterApp();
  } catch (e) { setMsg('网络错误，请重试', true); }
  finally { $('authBtn').disabled = false; }
}
async function logout() {
  try { await fetch('/api/logout', { method: 'POST' }); } catch (e) {}
  if (WS) { try { WS.close(); } catch (e) {} }
  location.reload();
}

/* ---------------- 个人中心：昵称 / 密码 ---------------- */
function openProfile() {
  if (!ME) return;
  $('pfUsername').textContent = ME.username;
  $('pfNick').value = ME.nickname || '';
  $('pfOld').value = '';
  $('pfNew').value = '';
  renderPfAvatar();
  renderNotifyUI();
  $('profileModal').classList.add('on');
}
function renderPfAvatar() {
  const el = $('pfAvatar'); if (!el) return;
  const u = avUrlOf(ME && ME.avatar);
  if (u) { el.style.backgroundImage = `url('${u}')`; el.textContent = ''; }
  else { el.style.backgroundImage = ''; el.textContent = '点击上传'; }
}
async function uploadAvatar(file) {
  if (!file) return;
  if (file.size > 2 * 1024 * 1024) return toast('头像需 2MB 以内');
  toast('🖼 正在上传头像…');
  try {
    const fd = new FormData(); fd.append('file', file);
    const r = await fetch('/api/profile/avatar', { method: 'POST', body: fd });
    const d = await r.json();
    if (!d.ok) return toast('❌ ' + (d.error || '上传失败'));
    ME.avatar = d.avatar;
    renderPfAvatar();
    toast('✅ 头像已更新');
    loadUsers();
  } catch (e) { toast('❌ 上传失败'); }
}
function closeProfile() {
  $('profileModal').classList.remove('on');
}
async function saveNickname() {
  const nick = $('pfNick').value.trim();
  if (!nick) return toast('请输入昵称');
  try {
    const r = await fetch('/api/profile', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ nickname: nick }),
    });
    const d = await r.json();
    if (!d.ok) return toast(d.error || '保存失败');
    ME.nickname = nick;
    toast('昵称已更新');
    closeProfile();
    loadUsers();
  } catch (e) { toast('网络错误'); }
}
async function savePassword() {
  const oldP = $('pfOld').value, newP = $('pfNew').value;
  if (!oldP || !newP) return toast('请填写原密码和新密码');
  if (newP.length < 6) return toast('新密码至少 6 位');
  try {
    const r = await fetch('/api/profile', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ oldPassword: oldP, newPassword: newP }),
    });
    const d = await r.json();
    if (!d.ok) return toast(d.error || '修改失败');
    toast('密码已修改，请重新登录');
    setTimeout(() => location.reload(), 1300);
  } catch (e) { toast('网络错误'); }
}

/* ---------------- 搜索聊天记录 ---------------- */
let searchState = { q: '', type: '', sender: '', from: 0, to: 0 };
let searchTimer = null;

function openSearch() {
  searchState = { q: '', type: '', sender: '', from: 0, to: 0 };
  const kw = $('searchKw');
  kw.value = '';
  kw.placeholder = '搜索文本消息关键词…';
  $('fileTag').classList.remove('on');
  $('fMedia').classList.remove('on');
  $('fFile').classList.remove('on');
  $('fSender').classList.remove('on');
  $('fDate').classList.remove('on');
  $('senderPicker').classList.add('hidden');
  $('datePicker').classList.add('hidden');
  $('dateFrom').value = '';
  $('dateTo').value = '';
  // 私聊会话：隐藏“群成员”筛选（此时搜索范围=当前私聊）
  const inPm = currentRoom !== 'group';
  $('fSender').style.display = inPm ? 'none' : '';
  $('searchResults').innerHTML = '<div class="search-empty">输入关键词或点筛选按钮开始搜索</div>';
  $('searchModal').classList.add('on');
  setTimeout(() => kw.focus(), 60);
}
function closeSearch() { $('searchModal').classList.remove('on'); }

function setType(t) {
  searchState.type = (searchState.type === t) ? '' : t;
  $('fMedia').classList.toggle('on', searchState.type === 'image');
  $('fFile').classList.toggle('on', searchState.type === 'file');
  $('fileTag').classList.toggle('on', searchState.type === 'file');
  $('searchKw').placeholder = (searchState.type === 'file') ? '输入文件名关键词…' : '搜索文本消息关键词…';
  runSearch();
}

function toggleSender() {
  const pk = $('senderPicker');
  pk.classList.toggle('hidden');
  $('fSender').classList.toggle('on', !pk.classList.contains('hidden'));
  if (!pk.classList.contains('hidden')) renderSenderPicker();
}
function renderSenderPicker() {
  const map = new Map();
  allUsers.forEach(u => { const n = dn(u); if (n) map.set(n, n); });
  onlineUsers.forEach(u => { const n = dn(u); if (n) map.set(n, n); });
  let html = `<button class="p-chip ${!searchState.sender ? 'on' : ''}" data-name="">全部</button>`;
  map.forEach(name => {
    html += `<button class="p-chip ${searchState.sender === name ? 'on' : ''}" data-name="${esc(name)}">${esc(name)}</button>`;
  });
  $('senderPicker').innerHTML = html;
}
function pickSender(name) {
  searchState.sender = name || '';
  $('fSender').classList.toggle('on', !!name);
  renderSenderPicker();
  runSearch();
}

function toggleDate() {
  const dp = $('datePicker');
  dp.classList.toggle('hidden');
  $('fDate').classList.toggle('on', !dp.classList.contains('hidden'));
}

async function runSearch() {
  const st = searchState;
  const p = new URLSearchParams();
  if (st.q) p.set('q', st.q);
  if (st.type) p.set('type', st.type);
  if (st.sender) p.set('sender', st.sender);
  if (st.from) p.set('from', st.from);
  if (st.to) p.set('to', st.to);
  if (currentRoom) p.set('room', currentRoom);
  p.set('limit', '50');
  const res = $('searchResults');
  res.innerHTML = '<div class="search-empty">搜索中…</div>';
  try {
    const r = await fetch('/api/search?' + p.toString());
    const d = await r.json();
    if (!d.ok) { res.innerHTML = `<div class="search-empty">${esc(d.error || '搜索失败')}</div>`; return; }
    if (!d.messages.length) { res.innerHTML = '<div class="search-empty">没有找到相关记录</div>'; return; }
    renderSearchResults(d.messages);
  } catch (e) { res.innerHTML = '<div class="search-empty">网络错误</div>'; }
}
function renderSearchResults(list) {
  const res = $('searchResults');
  res.innerHTML = '';
  list.forEach(m => {
    const el = document.createElement('div');
    el.className = 'sr-item';
    const preview = m.type === 'text'
      ? esc(m.content)
      : m.type === 'video'
        ? `<span class="sr-file">🎬 ${esc(m.fileName || '视频')}</span>`
      : m.type === 'file'
        ? `<span class="sr-file">📎 ${esc(m.fileName || '文件')}</span>`
        : `<span class="sr-file">🖼 图片</span>`;
    el.innerHTML = `
      <div class="sr-top">
        <span class="sr-name">${esc(m.fromName)}</span>
        <span class="sr-room">${esc(m.roomName || '')}</span>
        <span class="sr-time">${fmtTime(m.createdAt)}</span>
      </div>
      <div class="sr-content">${preview}</div>`;
    el.onclick = () => {
      closeSearch();
      if (m.room !== currentRoom) {
        if (m.room === 'group') openRoom('group');
        else openRoom('pm', m.peerId, m.peerName || '私聊');
      }
    };
    res.appendChild(el);
  });
}

$('searchKw').addEventListener('input', e => {
  searchState.q = e.target.value.trim();
  clearTimeout(searchTimer);
  searchTimer = setTimeout(runSearch, 300);
});
$('dateFrom').addEventListener('change', e => { searchState.from = e.target.value ? new Date(e.target.value + 'T00:00:00').getTime() : 0; runSearch(); });
$('dateTo').addEventListener('change', e => { searchState.to = e.target.value ? new Date(e.target.value + 'T23:59:59.999').getTime() : 0; runSearch(); });
$('senderPicker').addEventListener('click', e => {
  const b = e.target.closest('.p-chip');
  if (b) pickSender(b.dataset.name || '');
});

/* ---------------- 表情包共享库 ---------------- */
let stickerSort = 'hot';
function openStickers() {
  $('stickerModal').classList.add('on');
  if (!$('stickerGrid').dataset.loaded) loadStickers();
}
function closeStickers() { $('stickerModal').classList.remove('on'); }
function setStickerSort(s) {
  stickerSort = s;
  $('stkHot').classList.toggle('on', s === 'hot');
  $('stkNew').classList.toggle('on', s === 'time');
  loadStickers();
}
async function loadStickers() {
  const g = $('stickerGrid');
  g.innerHTML = '<div class="search-empty">加载中…</div>';
  try {
    const r = await fetch('/api/stickers?sort=' + stickerSort + '&limit=120');
    const d = await r.json();
    if (!d.ok) { g.innerHTML = `<div class="search-empty">${esc(d.error || '加载失败')}</div>`; return; }
    g.dataset.loaded = '1';
    renderStickers(d.stickers || [], d.isAdmin);
  } catch (e) { g.innerHTML = '<div class="search-empty">网络错误</div>'; }
}
function renderStickers(list, isAdmin) {
  const g = $('stickerGrid');
  if (!list.length) { g.innerHTML = '<div class="search-empty">还没有表情包，点上方「＋ 上传」添加</div>'; return; }
  g.innerHTML = '';
  list.forEach(s => {
    const el = document.createElement('div');
    el.className = 'stk-item';
    el.title = `已发送 ${s.sendCount} 次`;
    el.innerHTML = `<img src="${esc(s.url)}" alt="" loading="lazy"><span class="stk-count">${s.sendCount}</span>`;
    el.querySelector('img').onclick = () => sendSticker(s);
    if (isAdmin) {
      const del = document.createElement('button');
      del.className = 'stk-del'; del.textContent = '×'; del.title = '删除表情';
      del.onclick = (e) => { e.stopPropagation(); deleteSticker(s); };
      el.appendChild(del);
    }
    g.appendChild(el);
  });
}
function sendSticker(s) {
  if (!WS || WS.readyState !== 1) return toast('连接断开，正在重连…');
  const payload = { type: 'chat', msgType: 'sticker', stickerId: s.id };
  if (currentRoom !== 'group' && currentPeer) payload.to = currentPeer.id;
  WS.send(JSON.stringify(payload));
  closeStickers();
}
async function uploadSticker(file) {
  if (file.size > 2 * 1024 * 1024) return toast('❌ 表情包需 2MB 以内');
  toast('⏳ 正在上传表情…');
  const fd = new FormData(); fd.append('file', file);
  try {
    const r = await fetch('/api/stickers', { method: 'POST', body: fd });
    const d = await r.json();
    if (!d.ok) return toast('❌ ' + (d.error || '上传失败'));
    toast(d.dedup ? '该表情已存在，已加入库' : '✅ 表情已上传');
    loadStickers();
  } catch (e) { toast('❌ 上传失败'); }
}
async function deleteSticker(s) {
  if (!confirm('确定删除该表情？将从表情库移除（已发的历史消息不受影响）。')) return;
  try {
    const r = await fetch('/api/stickers/' + s.id, { method: 'DELETE' });
    const d = await r.json();
    if (!d.ok) return toast('❌ ' + (d.error || '删除失败'));
    toast('已删除');
    loadStickers();
  } catch (e) { toast('❌ 删除失败'); }
}
$('stkInput').addEventListener('change', e => {
  const f = e.target.files[0]; e.target.value = '';
  if (f) uploadSticker(f);
});

/* ---------------- 标题提醒：后台收到消息时闪烁 ---------------- */
const BASE_TITLE = document.title;
let titleTimer = null, titleOn = false, bgUnread = 0;
function renderTitle() {
  if (bgUnread > 0 && document.hidden) {
    if (!titleTimer) {
      titleTimer = setInterval(() => {
        titleOn = !titleOn;
        document.title = titleOn ? `【你有 ${bgUnread > 99 ? '99+' : bgUnread} 条新消息】` : BASE_TITLE;
      }, 1000);
    }
  } else {
    if (titleTimer) { clearInterval(titleTimer); titleTimer = null; }
    titleOn = false;
    document.title = BASE_TITLE;
  }
}
function bgBump() { bgUnread++; renderTitle(); }
function bgClear() { if (bgUnread === 0) return; bgUnread = 0; renderTitle(); }
document.addEventListener('visibilitychange', () => { if (document.hidden) renderTitle(); else bgClear(); });
window.addEventListener('focus', bgClear);

/* ================= 页面级系统通知（Windows 横幅） =================
   标签页开着（含最小化、切到别的软件）时，用浏览器原生通知弹 Windows 横幅：
   标题=发送人，正文=消息内容（图片/文件显示 [图片]/[文件]）；3 秒后由页面 close() 收起；
   silent 静音。个人中心可开关与切换「具体内容 / 你有X条新消息」。 */
const NOTIFY_KEY = 'chat_notify_pref';
const NOTIFY_MS = 3000;
let notifyNewCount = 0;                 // 「你有 X 条新消息」计数
let notifySnooze = false;               // 本次会话已选「以后再说」
let naOkFn = null, naNoFn = null;

function notifySupported() { return typeof Notification !== 'undefined'; }
function notifyPerm() { return notifySupported() ? Notification.permission : 'unsupported'; }
function isDesktopUA() { return !/Android|iPhone|iPad|iPod|Windows Phone|Mobile/i.test(navigator.userAgent || ''); }
function notifyPref() {
  try {
    const o = JSON.parse(localStorage.getItem(NOTIFY_KEY) || '{}') || {};
    return { on: o.on !== false, mode: o.mode === 'summary' ? 'summary' : 'detail' };
  } catch (e) { return { on: true, mode: 'detail' }; }
}
function saveNotifyPref(p) { try { localStorage.setItem(NOTIFY_KEY, JSON.stringify(p)); } catch (e) {} }

/* 弹一条系统通知：静音、3 秒后自动收起、点击跳到对应房间 */
function showNotify(title, body, opt) {
  const o = opt || {};
  if (!notifySupported() || Notification.permission !== 'granted') return null;
  if (!notifyPref().on) return null;
  if (!isDesktopUA()) return null;        // 手机浏览器多数不支持 new Notification()
  let n = null;
  try {
    n = new Notification(title, {
      body: body || '', silent: true, requireInteraction: false,
      tag: o.tag || 'chat', data: { url: o.url || '/' },
    });
  } catch (e) { return null; }
  const closeIt = () => { try { n.close(); } catch (e) {} };
  setTimeout(closeIt, NOTIFY_MS);
  if (o.onclick) n.onclick = () => { try { window.focus(); } catch (e) {} try { o.onclick(); } catch (e) {} closeIt(); };
  n.onerror = () => {};
  return n;
}
/* 点通知 → 跳到对应房间 */
function openNotifyTarget(m) {
  if (!m) return;
  if (m.room === 'group') { openRoom('group'); return; }
  const peerId = (ME && m.fromId === ME.id) ? m.toId : m.fromId;
  const u = (onlineUsers || []).find(x => x.id === peerId) || (allUsers || []).find(x => x.id === peerId);
  openRoom('pm', peerId, dn(u) || m.fromName || '');
}
/* 收到新消息时弹通知：自己发的、正在看该房间的不弹 */
function notifyIncoming(m) {
  if (!m || (ME && m.fromId === ME.id)) return;
  if (!notifySupported() || Notification.permission !== 'granted') return;
  const p = notifyPref();
  if (!p.on) return;
  if (m.room === currentRoom && !document.hidden) return;
  notifyNewCount++;
  if (p.mode === 'summary') {
    showNotify('聊天室有新消息', `你有 ${notifyNewCount} 条新消息`, { tag: 'chat-summary', onclick: () => openNotifyTarget(m) });
    return;
  }
  const group = m.room === 'group';
  const atMe = group && mentionHitsMe(m);
  const title = group ? (atMe ? `${m.fromName} 在群聊 @了你` : `${m.fromName} · 群聊`) : `${m.fromName} · 私聊`;
  showNotify(title, previewOf(m, 60), { tag: 'room:' + m.room, onclick: () => openNotifyTarget(m) });
}

/* ---- 权限申请弹窗 ---- */
const NA_GUIDE = '点击浏览器地址右方的铃铛图标按钮，再点击「允许此网站发送通知」以打开通知权限';
function hideNotifyAsk() { const el = $('notifyAsk'); if (el) el.classList.remove('on'); }
/* 提示图：只在真正要显示卡片时才加载（图片较大，避免每次打开页面都下载） */
function ensureNotifyImg() {
  const img = $('naImg'); if (!img) return;
  if (!img.getAttribute('src')) { const s = img.getAttribute('data-src'); if (s) img.setAttribute('src', s); }
}
/* 点提示图 → 用大图查看器放大 */
function enlargeNotifyImg(el) {
  const s = el && (el.getAttribute('src') || el.getAttribute('data-src'));
  if (s) showImg(s);
}
function showNotifyAsk(title, sub, okText, okFn, noText, noFn) {
  const el = $('notifyAsk'); if (!el) return;
  $('naTitle').textContent = title;
  $('naSub').textContent = sub || '';
  ensureNotifyImg();
  naOkFn = okFn || null; naNoFn = noFn || null;
  const ok = $('naOk'), no = $('naNo');
  ok.textContent = okText || '允许通知'; ok.style.display = naOkFn ? '' : 'none';
  if (noText) { no.textContent = noText; no.style.display = ''; } else { no.style.display = 'none'; }
  el.classList.add('on');
}
function requestNotifyPermission() {
  return new Promise(resolve => {
    try {
      const r = Notification.requestPermission(p => resolve(p || Notification.permission));
      if (r && typeof r.then === 'function') r.then(x => resolve(x || Notification.permission)).catch(() => resolve(Notification.permission));
    } catch (e) { resolve(Notification.permission); }
  });
}
function notifyTest() {
  notifyNewCount = 0;
  showNotify('✅ 系统通知已开启', '以后有新消息会像这样提醒（3 秒自动收起 · 无提示音）', { tag: 'chat-test' });
}
/* 打开页面 / 手动开启时申请权限 */
async function askNotifyPermission() {
  if (!notifySupported() || !isDesktopUA()) return hideNotifyAsk();
  if (Notification.permission === 'denied') {
    showNotifyAsk('通知权限已被拒绝', '请点地址栏左侧的 🔒 图标 → 网站设置 → 通知，改为「允许」后刷新页面', '知道了', hideNotifyAsk, '');
    return;
  }
  if (Notification.permission === 'granted') { hideNotifyAsk(); renderNotifyUI(); notifyTest(); return; }
  showNotifyAsk('🔔 打开通知权限', NA_GUIDE, '允许通知', () => askNotifyPermission(), '以后再说');
  const p = await requestNotifyPermission();
  if (notifySnooze) return;                 // 期间点了「以后再说」→ 不再打扰
  renderNotifyUI();
  if (p === 'granted') { hideNotifyAsk(); notifyTest(); }
  else if (p === 'denied') showNotifyAsk('通知权限已被拒绝', '请点地址栏左侧的 🔒 图标 → 网站设置 → 通知，改为「允许」后刷新页面', '知道了', hideNotifyAsk, '');
  else showNotifyAsk('还没有拿到通知权限', NA_GUIDE, '重新申请', () => askNotifyPermission(), '以后再说');
}
function initNotify() {                     // 进app时调用
  renderNotifyUI();
  const p = notifyPref();
  if (!p.on || notifySnooze) return;
  if (!notifySupported() || !isDesktopUA()) return;
  if (Notification.permission === 'default') askNotifyPermission();   // 默认开启：打开页面就申请
}
/* ---- 个人中心里的开关与文案 ---- */
function notifyStateText() {
  if (!notifySupported()) return '当前浏览器不支持系统通知';
  if (!isDesktopUA()) return '手机浏览器多数不支持页面通知，建议用电脑版浏览器';
  const p = notifyPerm();
  if (p === 'granted') return '已授权 · 新消息弹 Windows 横幅，3 秒自动收起，无提示音';
  if (p === 'denied') return '已被浏览器拒绝，请到地址栏左侧站点设置里改为「允许」';
  return '尚未授权，打开右侧开关即可申请权限';
}
function renderNotifyUI() {
  const sw = $('pfNotifySw'); if (!sw) return;
  const p = notifyPref();
  sw.classList.toggle('on', !!p.on);
  const d = $('nfDetail'), s = $('nfSummary');
  if (d && s) { d.classList.toggle('on', p.mode === 'detail'); s.classList.toggle('on', p.mode === 'summary'); }
  const st = $('notifyState'); if (st) st.textContent = notifyStateText();
}
function toggleNotifyPref() {
  const p = notifyPref();
  p.on = !p.on; saveNotifyPref(p); renderNotifyUI();
  notifyNewCount = 0;
  if (!p.on) { hideNotifyAsk(); return toast('已关闭系统通知提醒'); }
  if (!isDesktopUA()) return toast('手机浏览器多数不支持，请在电脑上使用');
  if (notifySupported() && Notification.permission === 'granted') return notifyTest();
  askNotifyPermission();
}
function setNotifyMode(mode) {
  const p = notifyPref();
  p.mode = mode === 'summary' ? 'summary' : 'detail';
  saveNotifyPref(p); renderNotifyUI();
  toast(p.mode === 'summary' ? '通知只显示「你有 X 条新消息」' : '通知显示发送人和消息内容');
}
(function bindNotifyAsk() {
  const ok = $('naOk'), no = $('naNo');
  if (ok) ok.addEventListener('click', () => { const f = naOkFn || hideNotifyAsk; f(); });
  if (no) no.addEventListener('click', () => { notifySnooze = true; const f = naNoFn || hideNotifyAsk; f(); });
})();
window.addEventListener('focus', () => { notifyNewCount = 0; });
document.addEventListener('visibilitychange', () => { if (!document.hidden) notifyNewCount = 0; });

/* ---------------- @提及 ---------------- */
function renderContent(m) {
  let s = esc(m.content || '');
  const ms = Array.isArray(m.mentions) ? m.mentions : [];
  if (ms.length) {
    const names = [];
    ms.forEach(x => {
      if (x === 'all') { names.push('所有人'); return; }
      const u = (onlineUsers || []).find(v => v.id === x) || (allUsers || []).find(v => v.id === x);
      if (u) names.push(dn(u));
    });
    [...new Set(names)].forEach(n => {
      const en = esc('@' + n);
      if (s.indexOf(en) >= 0) s = s.split(en).join(`<span class="mention">${en}</span>`);
    });
  }
  return s.replace(/\n/g, '<br>');
}
function mentionPopOpen() { const p = $('mentionPop'); return !!(p && p.classList.contains('on')); }
function hideMentionPop() { const p = $('mentionPop'); if (p) p.classList.remove('on'); mentionIdx = -1; }
function mentionMembers() {
  const out = [], seen = new Set();
  const add = (u, online) => {
    if (!u || (ME && u.id === ME.id) || seen.has(u.id)) return;
    seen.add(u.id); out.push({ id: u.id, name: dn(u), avatar: u.avatar || '', online: !!online });
  };
  (onlineUsers || []).forEach(u => add(u, true));
  (allUsers || []).forEach(u => add(u, false));
  out.sort((a, b) => (b.online ? 1 : 0) - (a.online ? 1 : 0) || a.name.localeCompare(b.name, 'zh'));
  return out;
}
function mpRow(name, id, avatar) {
  const el = document.createElement('div');
  el.className = 'mp-item';
  const av = document.createElement('div');
  av.className = 'mp-avwrap';
  av.innerHTML = (id === 'all') ? '<div class="avatar mp-av">@</div>' : avatarHTML(name, avatar, false);
  const nm = document.createElement('div'); nm.className = 'mp-name'; nm.textContent = name;
  el.appendChild(av); el.appendChild(nm);
  el.addEventListener('click', () => insertMention(name, id));
  return el;
}
function showMentionPop(query) {
  const pop = $('mentionPop'); if (!pop) return;
  const q = String(query || '').toLowerCase();
  const members = mentionMembers().filter(u => !q || u.name.toLowerCase().includes(q));
  const allHit = !q || '所有人'.includes(q);
  pop.innerHTML = ''; mentionItems = [];
  if (allHit) { pop.appendChild(mpRow('所有人', 'all', '')); mentionItems.push({ name: '所有人', id: 'all' }); }
  if (members.length) {
    const h = document.createElement('div'); h.className = 'mp-head'; h.textContent = '群成员';
    pop.appendChild(h);
    members.slice(0, 60).forEach(u => { pop.appendChild(mpRow(u.name, u.id, u.avatar)); mentionItems.push({ name: u.name, id: u.id }); });
  } else if (q && !allHit) {
    const e = document.createElement('div'); e.className = 'mp-empty'; e.textContent = '没有匹配的成员';
    pop.appendChild(e);
  }
  mentionIdx = mentionItems.length ? 0 : -1;
  markMentionIdx();
  pop.classList.add('on');
}
function markMentionIdx() {
  const pop = $('mentionPop'); if (!pop) return;
  [...pop.querySelectorAll('.mp-item')].forEach((el, i) => el.classList.toggle('on', i === mentionIdx));
  const cur = pop.querySelector('.mp-item.on');
  if (cur && cur.scrollIntoView) { try { cur.scrollIntoView({ block: 'nearest' }); } catch (e) {} }
}
function moveMention(d) {
  if (!mentionItems.length) return;
  mentionIdx = (mentionIdx + d + mentionItems.length) % mentionItems.length;
  markMentionIdx();
}
function pickMentionAt(i) { const it = mentionItems[i]; if (it) insertMention(it.name, it.id); }
function checkMentionTrigger() {
  const inp = $('input'); if (!inp) return;
  const pos = inp.selectionStart ?? 0;
  const m = /@([^\s@]{0,20})$/.exec(inp.value.slice(0, pos));
  if (!m) return hideMentionPop();
  showMentionPop(m[1]);
}
function insertMention(name, id) {
  const inp = $('input'); if (!inp) return;
  const v = inp.value, pos = inp.selectionStart ?? v.length;
  const m = /@([^\s@]*)$/.exec(v.slice(0, pos));
  const start = m ? pos - m[0].length : pos;
  const text = '@' + name + ' ';
  inp.value = v.slice(0, start) + text + v.slice(pos);
  const caret = start + text.length;
  try { inp.setSelectionRange(caret, caret); } catch (e) {}
  mentionPick.set(name, id);
  hideMentionPop();
  inp.focus();
  inp.style.height = 'auto'; inp.style.height = Math.min(inp.scrollHeight, 110) + 'px';
}
/* 浮动菜单（目前仅用于「撤回」；@某人 改为长按/右键头像直接插入） */
let atMenuShownAt = 0;
const RECALL_MS = 120000;                     // 撤回时限：2 分钟
function showFloatMenu(x, y, actions) {
  const m = $('atMenu'); if (!m || !actions || !actions.length) return;
  m.innerHTML = '';
  actions.forEach(a => {
    const b = document.createElement('button');
    b.className = 'at-menu-btn' + (a.cls ? ' ' + a.cls : '');
    b.textContent = a.label;
    b.addEventListener('click', () => { hideAtMenu(); a.fn(); });
    m.appendChild(b);
  });
  m.style.left = Math.max(6, Math.min(x, window.innerWidth - 160)) + 'px';
  m.style.top = Math.max(6, y - 46) + 'px';
  m.classList.add('on'); atMenuShownAt = Date.now();
}
function hideAtMenu() { const m = $('atMenu'); if (m) m.classList.remove('on'); }

/* ---------------- 撤回消息 ---------------- */
function recallSelfHTML() { return '<div class="recall-line"><span class="recall-tip">你撤回了一条消息</span><span class="recall-edit">重新编辑</span></div>'; }
function recallAdminNote() { return '<div class="recall-tip">用户撤回了以上信息</div>'; }
function recallOtherHTML() { return '<div class="recall-tip">对方撤回了一条消息</div>'; }
function recallMsg(id) {
  if (!WS || WS.readyState !== 1) return toast('连接断开，正在重连…');
  WS.send(JSON.stringify({ type: 'recall', id }));
}
/* 收到撤回事件：就地更新已渲染的那条消息 */
function applyRecall(m) {
  const el = document.querySelector(`#msgs .msg[data-id="${m.id}"]`);
  if (!el || el.dataset.recalled === '1') return;
  el.dataset.recalled = '1';
  const isSelf = ME && Number(el.dataset.fromId) === ME.id;
  const body = el.querySelector('.msg-body'); if (!body) return;
  const timeEl = body.querySelector('.msg-time');
  const put = (html) => { if (timeEl) timeEl.insertAdjacentHTML('beforebegin', html); else body.insertAdjacentHTML('afterbegin', html); };
  if (isSelf) {
    [...body.children].forEach(c => { if (c !== timeEl) c.remove(); });
    put(recallSelfHTML());
  } else if (ME && ME.isAdmin) {
    put(recallAdminNote());                        // 管理员：保留原文，仅追加灰字
  } else {
    [...body.children].forEach(c => { if (c !== timeEl) c.remove(); });
    put(recallOtherHTML());
  }
}
/* 点「重新编辑」→ 把撤回的文本放回输入框 */
function insertRecalledText(text) {
  if (!text) return toast('原消息内容不可用');
  const inp = $('input');
  const v = inp.value.replace(/\s*$/, '');
  inp.value = v ? (v + ' ' + text) : text;
  try { inp.setSelectionRange(inp.value.length, inp.value.length); } catch (e) {}
  inp.focus();
  inp.style.height = 'auto'; inp.style.height = Math.min(inp.scrollHeight, 110) + 'px';
}

function enterApp() {
  $('login').style.display = 'none';
  $('app').classList.add('on');
  if (ME.isAdmin) $('adminEntry').style.display = 'flex';
  connectWS();
  loadUnread();
  initNotify();                        // 默认开启：打开页面就申请系统通知权限
  loadUsers();
  setInterval(loadUsers, 60000);      // 定期刷新用户列表（新注册/离线变化）
  openRoom('group');
}

/* ---------------- WebSocket ---------------- */
function connectWS() {
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  WS = new WebSocket(`${proto}//${location.host}/ws`);
  WS.onopen = () => {
    $('chStatus').innerHTML = '<span class="live">已连接</span> · 消息实时同步';
    clearInterval(pingTimer);
    pingTimer = setInterval(() => { if (WS && WS.readyState === 1) WS.send(JSON.stringify({ type: 'ping' })); }, 25000);
    loadUnread();                                  // 重连/休眠唤醒后刷新未读与「有人@我」
  };
  WS.onmessage = (ev) => {
    let m; try { m = JSON.parse(ev.data); } catch (e) { return; }
    if (m.type === 'online') { onlineUsers = m.users || []; renderOnline(m); return; }
    if (m.type === 'read') {
      readState[m.userId] = { username: m.username, lastReadId: m.lastReadId };
      updateReadLabels();
      return;
    }
    if (m.type === 'chat') { onIncoming(m); return; }
    if (m.type === 'recall') { applyRecall(m); return; }
    if (m.type === 'recall_fail') { toast(m.reason || '撤回失败'); return; }
    if (m.type === 'ready') { ME = { ...ME, id: m.userId, username: m.username, nickname: m.nickname }; return; }
  };
  WS.onclose = () => {
    $('chStatus').innerHTML = '<span style="color:#EF4444">连接断开，重连中…</span>';
    clearInterval(pingTimer);
    clearTimeout(reconnectTimer);
    reconnectTimer = setTimeout(connectWS, 2500);
  };
  WS.onerror = () => {};
}

/* ---------------- 未读 ---------------- */
async function loadUnread() {
  try {
    const r = await fetch('/api/unread'); const d = await r.json();
    if (d.ok) { unread = d.unread || {}; mentionGroup = !!d.mentionGroup; renderUsers(); }
  } catch (e) {}
}
async function markRead(room) {
  delete unread[room];
  renderUsers();
  try {
    const r = await fetch('/api/read', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ room }),
    });
    const d = await r.json();
    if (d.ok) { if (d.unread) unread = d.unread; mentionGroup = !!d.mentionGroup; renderUsers(); }
  } catch (e) {}
}
function unreadOf(room) { return unread[room] || 0; }
function dotHTML(n) { return n > 0 ? `<span class="unread-dot">${n > 99 ? '99+' : n}</span>` : ''; }

/* ---------------- 已读回执 ---------------- */
async function loadReadState(room) {
  try {
    const r = await fetch('/api/read-state?room=' + encodeURIComponent(room));
    const d = await r.json();
    if (!d.ok) return;
    readState = {};
    (d.states || []).forEach(s => { readState[s.userId] = { username: s.username, lastReadId: s.lastReadId }; });
    updateReadLabels();
  } catch (e) {}
}
/* 计算某条消息的已读文案（仅自己发的消息需要） */
function readLabelFor(msgId) {
  if (!ME) return '';
  const readers = Object.entries(readState)
    .filter(([uid, v]) => Number(uid) !== ME.id && v.lastReadId >= msgId)
    .map(([, v]) => v.username);
  if (!readers.length) return '';
  return currentRoom === 'group' ? '已读：' + readers.join('，') : '已读';
}
/* 已读状态变化后，刷新当前屏内自己发的消息 */
function updateReadLabels() {
  document.querySelectorAll('#msgs .msg.self').forEach(el => {
    const id = Number(el.dataset.id);
    const span = el.querySelector('.msg-read');
    if (span) span.textContent = readLabelFor(id);
  });
}

/* 拉取全部用户（含离线） */
async function loadUsers() {
  try {
    const r = await fetch('/api/users'); const d = await r.json();
    if (d.ok) { allUsers = d.users || []; renderUsers(); }
  } catch (e) {}
}
function fmtAgo(ts) {
  if (!ts) return '从未登录';
  const s = Math.floor((Date.now() - ts) / 1000);
  if (s < 60) return '刚刚活跃';
  if (s < 3600) return Math.floor(s / 60) + ' 分钟前活跃';
  if (s < 86400) return Math.floor(s / 3600) + ' 小时前活跃';
  if (s < 2592000) return Math.floor(s / 86400) + ' 天前活跃';
  return new Date(ts).toLocaleDateString('zh-CN');
}

/* ---------------- 在线列表 ---------------- */
function renderOnline(snap) {
  if (snap) {
    $('onlineNum').textContent = snap.onlineUsers ?? 0;
    $('deviceNum').textContent = snap.onlineDevices ?? 0;
  }
  if (currentRoom === 'group') {
    const n = (snap?.onlineUsers ?? onlineUsers.length) || 0;
    $('chStatus').innerHTML = `<span class="live">${n} 人在线</span> · 消息实时同步`;
  }
  renderUsers();
}
function renderUsers() {
  const kw = ($('searchInput').value || '').trim().toLowerCase();
  const ipCount = {};
  onlineUsers.forEach(u => u.devices.forEach(d => { if (d.ip) ipCount[d.ip] = (ipCount[d.ip] || 0) + 1; }));
  const onlineIds = new Set(onlineUsers.map(u => u.id));

  let html = `<div class="user-item ${currentRoom === 'group' ? 'on' : ''}" onclick="openRoom('group')">
    <div class="avatar" style="background:linear-gradient(135deg,var(--primary),var(--primary-2))">群</div>
    <div class="u-meta"><div class="u-name">群聊大厅</div>
      <div class="u-sub">${mentionGroup ? '<span class="mention-me">[有人@我]</span>' : '所有在线用户'}</div></div>
    ${dotHTML(unreadOf('group')) || `<span class="u-badge">${onlineUsers.length}</span>`}</div>`;

  // ---------- 在线用户 ----------
  const onlineList = onlineUsers.filter(u => !kw || dn(u).toLowerCase().includes(kw));
  html += `<div class="sb-group">在线用户 · ${onlineUsers.length}</div>`;
  if (!onlineList.length) html += `<div class="empty-tip" style="padding:14px 0;font-size:12.5px">${kw ? '没有匹配的在线用户' : '暂无其他人在线'}</div>`;
  onlineList.forEach(u => {
    const self = ME && u.id === ME.id;
    const multiDev = u.devices.length > 1;
    const sharedIp = u.devices.some(d => d.ip && ipCount[d.ip] > 1);
    const room = ME ? pmRoom(ME.id, u.id) : '';
    const on = currentPeer && currentPeer.id === u.id;
    const devTxt = multiDev ? `${u.devices.length} 台设备` : (u.devices[0]?.ip || '在线');
    const n = unreadOf(room);
    const nm = dn(u);
    html += `<div class="user-item ${on ? 'on' : ''}" onclick="openRoom('pm', ${u.id}, '${esc(nm)}')">
      ${avatarHTML(nm, u.avatar, true)}
      <div class="u-meta"><div class="u-name">${esc(nm)}</div><div class="u-sub">${esc(devTxt)}</div></div>
      ${n > 0 ? dotHTML(n) : (self ? '<span class="self-tag">我</span>' : (sharedIp ? '<span class="u-badge">同IP</span>' : ''))}
    </div>`;
  });

  // ---------- 离线用户 ----------
  const offlineList = allUsers.filter(u => !onlineIds.has(u.id) && (!kw || dn(u).toLowerCase().includes(kw)));
  const offlineTotal = allUsers.filter(u => !onlineIds.has(u.id)).length;
  html += `<div class="sb-group">离线用户 · ${offlineTotal}</div>`;
  if (!offlineList.length) html += `<div class="empty-tip" style="padding:14px 0;font-size:12.5px">${kw ? '没有匹配的离线用户' : '暂无离线用户'}</div>`;
  offlineList.forEach(u => {
    const room = ME ? pmRoom(ME.id, u.id) : '';
    const on = currentPeer && currentPeer.id === u.id;
    const n = unreadOf(room);
    const nm = dn(u);
    html += `<div class="user-item offline ${on ? 'on' : ''}" onclick="openRoom('pm', ${u.id}, '${esc(nm)}')">
      ${avatarHTML(nm, u.avatar, false)}
      <div class="u-meta"><div class="u-name">${esc(nm)}</div><div class="u-sub">${esc(fmtAgo(u.lastSeen))}</div></div>
      ${n > 0 ? dotHTML(n) : '<span class="u-badge gray">离线</span>'}
    </div>`;
  });

  $('userList').innerHTML = html;
}

/* ---------------- 房间 & 历史（含上滑加载） ---------------- */
function openRoom(kind, peerId, peerName) {
  if (kind === 'group') { currentRoom = 'group'; currentPeer = null; }
  else {
    if (ME && peerId === ME.id) { toast('不能和自己私聊'); return; }
    currentRoom = pmRoom(ME.id, peerId);
    currentPeer = { id: peerId, username: peerName };
  }
  updateHead();
  loadHistory(currentRoom);
  loadReadState(currentRoom);
  markRead(currentRoom);
  renderUsers();
  closeSidebar();
}
function updateHead() {
  const av = $('chAvatar'), nm = $('chName'), st = $('chStatus');
  if (currentRoom === 'group') {
    av.textContent = '群'; av.style.background = 'linear-gradient(135deg,var(--primary),var(--primary-2))';
    nm.textContent = '群聊大厅';
    st.innerHTML = `<span class="live">${onlineUsers.length} 人在线</span> · 消息实时同步`;
  } else {
    const onU = onlineUsers.find(x => x.id === currentPeer?.id);        // 在线项（含 devices）
    const anyU = onU || allUsers.find(x => x.id === currentPeer?.id);    // 离线项：仅用于取头像
    const pu = currentPeer?.username || '?';
    const uAv = avUrlOf(anyU && anyU.avatar);
    if (uAv) { av.textContent = ''; av.style.background = ''; av.style.backgroundImage = `url('${uAv}')`; av.classList.add('avatar-pic'); }
    else { av.classList.remove('avatar-pic'); av.style.backgroundImage = ''; av.textContent = initial(pu); av.style.background = colorOf(pu); }
    nm.textContent = currentPeer?.username || '私聊';
    st.innerHTML = onU ? `<span class="live">在线</span> · ${esc(onU.devices[0]?.ip || '')}` : '对方已离线（消息仍会保存）';
  }
}
async function loadHistory(room) {
  $('msgs').innerHTML = '<div class="empty-tip">加载中…</div>';
  oldestId = null; hasMore = false; loadingMore = false;
  try {
    const r = await fetch(`/api/history?room=${encodeURIComponent(room)}&limit=${PAGE_SIZE}`);
    const d = await r.json();
    if (!d.ok) { $('msgs').innerHTML = `<div class="empty-tip">${esc(d.error || '加载失败')}</div>`; return; }
    const box = $('msgs'); box.innerHTML = '';
    if (!d.messages.length) box.innerHTML = `<div class="empty-tip">${room === 'group' ? '还没有消息，来说第一句吧 👋' : '还没有私聊记录'}</div>`;
    else d.messages.forEach(m => box.appendChild(buildMsgEl(m)));
    oldestId = d.messages[0]?.id || null;
    hasMore = !!d.hasMore;
    autoStick = true;
    scrollBottom();
    // 图片尺寸逐步就绪后再贴一次底（防止高度变化把位置顶上去）
    setTimeout(() => { if (autoStick) scrollBottom(); }, 300);
  } catch (e) { $('msgs').innerHTML = '<div class="empty-tip">加载失败</div>'; }
}

/* 上滑加载更早的消息（保持滚动位置） */
async function loadMore() {
  if (!hasMore || loadingMore || !oldestId) return;
  loadingMore = true;
  const box = $('msgs');
  const prevH = box.scrollHeight, prevTop = box.scrollTop;
  const tip = document.createElement('div');
  tip.className = 'day-divider load-more-tip'; tip.innerHTML = '<span>正在加载更早的消息…</span>';
  box.insertBefore(tip, box.firstChild);
  try {
    const r = await fetch(`/api/history?room=${encodeURIComponent(currentRoom)}&before=${oldestId}&limit=${PAGE_SIZE}`);
    const d = await r.json();
    tip.remove();
    if (d.ok && d.messages.length) {
      const frag = document.createDocumentFragment();
      d.messages.forEach(m => frag.appendChild(buildMsgEl(m)));
      box.insertBefore(frag, box.firstChild);
      oldestId = d.messages[0].id;
      hasMore = !!d.hasMore;
      box.scrollTop = box.scrollHeight - prevH + prevTop;   // 保持视觉位置
    } else { hasMore = false; }
  } catch (e) { tip.remove(); }
  loadingMore = false;
}

/* ---------------- 消息渲染 ---------------- */
/* 可直接内嵌播放的视频：按扩展名判断，兼容旧消息（以前视频存在 type=file 里） */
const VIDEO_EXT = ['.mp4', '.webm', '.ogv', '.ogg', '.m4v', '.mov'];
function isPlayableVideo(m) {
  if (!m || !m.filePath) return false;
  const t = m.msgType || m.type || '';
  if (t === 'sticker') return false;
  if (t === 'video') return true;
  const p = String(m.filePath).split('?')[0].toLowerCase();
  return VIDEO_EXT.some(x => p.endsWith(x));
}
function buildMsgEl(m) {
  const isSelf = ME && m.fromId === ME.id;
  const showName = currentRoom === 'group' && !isSelf;
  const time = fmtTime(m.createdAt || Date.now());
  // 兼容两种来源：历史消息用 m.type，WebSocket 实时推送用 m.msgType
  const mtype = m.msgType || m.type || 'text';
  let inner = '';
  if (isPlayableVideo(m)) {
    inner = `<div class="bubble" style="padding:6px"><video class="bubble-video" src="${esc(m.filePath)}" controls preload="metadata" playsinline onloadedmetadata="onImgLoad()"></video></div>`;
  } else if (mtype === 'image' && m.filePath) {
    inner = `<div class="bubble" style="padding:6px"><img src="${esc(m.filePath)}" alt="" onload="onImgLoad()" onclick="showImg('${esc(m.filePath)}')"></div>`;
  } else if (mtype === 'sticker' && m.filePath) {
    inner = `<div class="sticker-bubble"><img src="${esc(m.filePath)}" alt="" onload="onImgLoad()"></div>`;
  } else if (mtype === 'file' && m.filePath) {
    inner = `<a class="bubble file-bubble" href="${esc(m.filePath)}" download="${esc(m.fileName || '')}" target="_blank">
      <div class="file-ico"><svg viewBox="0 0 24 24"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8l-6-6zm-1 7V3.5L18.5 9H13z"/></svg></div>
      <div><div class="file-name">${esc(m.fileName || '文件')}</div><div class="file-size">${fmtSize(m.fileSize)}</div></div></a>`;
  } else {
    inner = `<div class="bubble">${renderContent(m)}</div>`;
  }
  if (m.recalled) {                                  // 撤回后按角色渲染
    if (isSelf) inner = recallSelfHTML();
    else if (ME && ME.isAdmin) inner = inner + recallAdminNote();
    else inner = recallOtherHTML();
  }
  const el = document.createElement('div');
  el.className = 'msg ' + (isSelf ? 'self' : 'other');
  el.dataset.id = m.id;
  el.dataset.fromId = m.fromId;
  el.dataset.fromName = m.fromName || '';
  el.dataset.createdAt = m.createdAt || 0;
  if (m.recalled) el.dataset.recalled = '1';
  if (isSelf && m.content) el.dataset.content = m.content;   // 供「重新编辑」取用
  el.innerHTML = `
    ${avatarHTML(m.fromName || '?', m.fromAvatar)}
    <div class="msg-body">
      ${showName ? `<div class="msg-sender">${esc(m.fromName)}</div>` : ''}
      ${inner}
      <div class="msg-time">${time}${isSelf ? `<span class="msg-read">${readLabelFor(m.id)}</span>` : ''}</div>
    </div>`;
  return el;
}
function scrollBottom() { const b = $('msgs'); b.scrollTop = b.scrollHeight; autoStick = true; }
function showImg(src) { $('lightboxImg').src = src; $('lightbox').classList.add('on'); }
/* 图片加载完高度变化：如果用户本来就贴在底部，跟着保持在底部 */
function onImgLoad() { if (autoStick) { const b = $('msgs'); b.scrollTop = b.scrollHeight; } }

/* 实时消息预览文案：历史消息用 type，WS 推送用 msgType（原来只看 type 导致一律显示「[文件]」）
   max：文本截断长度，默认 18（toast 用），系统通知传 60 */
function previewOf(m, max) {
  const t = m.msgType || m.type || 'text';
  const n = Number(max) > 0 ? Number(max) : 18;
  if (t === 'image') return '[图片]';
  if (t === 'video') return '[视频]';
  if (t === 'sticker') return '[表情]';
  if (t === 'file') return isPlayableVideo(m) ? '[视频]' : '[文件]';   // 旧消息：视频存在 file 里
  return String(m.content || '').slice(0, n);
}
/* 这条群消息是否 @了我（含 @所有人） */
function mentionHitsMe(m) {
  const ms = Array.isArray(m.mentions) ? m.mentions : [];
  return ms.some(x => x === 'all' || (ME && Number(x) === ME.id));
}
function onIncoming(m) {
  const mine = ME && m.fromId === ME.id;
  if (!mine && document.hidden) bgBump();      // 标签页在后台 → 计数并闪烁标题
  notifyIncoming(m);                           // 页面级系统通知（内部自行判断是否正在看该房间）
  const inThisRoom = m.room === currentRoom;
  if (inThisRoom) {
    const box = $('msgs');
    const empty = box.querySelector('.empty-tip'); if (empty) empty.remove();
    box.appendChild(buildMsgEl(m));
    scrollBottom();
    if (!mine) markRead(currentRoom);          // 正在看这个房间 → 自动已读
    return;
  }
  // 不在当前房间 → 未读 +1
  if (!mine) {
    unread[m.room] = (unread[m.room] || 0) + 1;
    if (m.room === 'group' && mentionHitsMe(m)) mentionGroup = true;   // 有人@我 → 侧栏立即标红
    renderUsers();
  }
  if (m.room === 'group') {
    toast(`💬 ${m.fromName}：${previewOf(m)}`);
  } else if (!mine) {
    const peer = m.fromId === ME.id ? m.toId : m.fromId;
    const u = onlineUsers.find(x => x.id === peer);
    toast(`✉️ ${u ? (u.displayName || u.username) : '有人'} 给你发了私聊`);
  }
}

/* ---------------- 发送 ---------------- */
function onKey(e) {
  if (mentionPopOpen()) {                    // @选择框打开时优先处理导航/选择
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); moveMention(e.key === 'ArrowDown' ? 1 : -1); return; }
    if (e.key === 'Escape') { e.preventDefault(); hideMentionPop(); return; }
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); pickMentionAt(mentionIdx >= 0 ? mentionIdx : 0); return; }
  }
  if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); }
  const t = e.target; t.style.height = 'auto'; t.style.height = Math.min(t.scrollHeight, 110) + 'px';
}
function send() {
  const inp = $('input'); const v = inp.value.trim();
  if (!v) return;
  if (!WS || WS.readyState !== 1) return toast('连接断开，正在重连…');
  const payload = { type: 'chat', msgType: 'text', content: v };
  if (currentRoom !== 'group' && currentPeer) payload.to = currentPeer.id;
  if (currentRoom === 'group' && mentionPick.size) {         // 只带上文本里仍然存在的 @对象
    const ms = [];
    mentionPick.forEach((id, name) => { if (v.indexOf('@' + name) >= 0) ms.push(id); });
    if (ms.length) payload.mentions = ms;
  }
  WS.send(JSON.stringify(payload));
  inp.value = ''; inp.style.height = 'auto';
  mentionPick.clear();
  hideMentionPop();
}
async function uploadAndSend(file) {
  if (!file) return;
  if (file.size > 50 * 1024 * 1024) return toast('文件超过 50MB 限制');
  const tip = $('upTip'); tip.textContent = '上传中…';
  try {
    const fd = new FormData(); fd.append('file', file);
    const r = await fetch('/api/upload', { method: 'POST', body: fd });
    const d = await r.json();
    if (!d.ok) { tip.textContent = ''; return toast(d.error || '上传失败'); }
    tip.textContent = '';
    const payload = { type: 'chat', msgType: d.isVideo ? 'video' : (d.isImage ? 'image' : 'file'), content: '', file: { url: d.url, name: d.name, size: d.size } };
    if (currentRoom !== 'group' && currentPeer) payload.to = currentPeer.id;
    WS.send(JSON.stringify(payload));
  } catch (e) { tip.textContent = ''; toast('上传失败'); }
}

$('fileInput').addEventListener('change', e => { const f = e.target.files[0]; e.target.value = ''; if (f) uploadAndSend(f); });
$('anyInput').addEventListener('change', e => { const f = e.target.files[0]; e.target.value = ''; if (f) uploadAndSend(f); });
$('avatarInput').addEventListener('change', e => { const f = e.target.files[0]; e.target.value = ''; if (f) uploadAvatar(f); });

/* ---- @提及：输入框触发 ---- */
$('input').addEventListener('input', checkMentionTrigger);
$('input').addEventListener('click', checkMentionTrigger);
document.addEventListener('click', e => {
  if (!mentionPopOpen()) return;
  if (e.target.closest('#mentionPop') || e.target === $('input')) return;
  hideMentionPop();
});
document.addEventListener('click', e => {
  const m = $('atMenu'); if (!m || !m.classList.contains('on')) return;
  if (e.target.closest('#atMenu')) return;
  if (Date.now() - atMenuShownAt < 400) return;      // 长按后紧跟的 click 忽略
  hideAtMenu();
});
/* ---- 长按/右键他人头像 → 直接把 @对方 插入输入框（仅群聊） ---- */
let avMentionAt = 0, avMentionId = 0;
function mentionByAvatar(fromId, fromName) {
  if (currentRoom !== 'group') return false;                 // 只在群聊里 @
  const name = String(fromName || '').trim();
  if (!fromId || !name || (ME && fromId === ME.id)) return false;
  const now = Date.now();
  // 安卓长按会同时触发 touchstart 计时器与 contextmenu：同一次长按只插入一次
  if (fromId === avMentionId && now - avMentionAt < 900) return true;
  avMentionAt = now; avMentionId = fromId;
  insertMention(name, fromId);
  return true;
}
/* ---- 头像右键 / 自己的消息右键：@ 或 撤回 ---- */
$('msgs').addEventListener('contextmenu', e => {
  const msg = e.target.closest('.msg'); if (!msg || msg.dataset.recalled === '1') return;
  const id = Number(msg.dataset.id), fromId = Number(msg.dataset.fromId);
  if (ME && fromId === ME.id) {                       // 自己的消息 → 撤回
    if (Date.now() - Number(msg.dataset.createdAt || 0) > RECALL_MS) return;
    e.preventDefault();
    showFloatMenu(e.clientX, e.clientY, [{ label: '撤回', cls: 'danger', fn: () => recallMsg(id) }]);
    return;
  }
  if (!e.target.closest('.msg .avatar')) return;      // 仅他人头像
  if (!mentionByAvatar(fromId, msg.dataset.fromName)) return;
  e.preventDefault();
  hideAtMenu();
});
/* ---- 手机：长按 ---- */
let atLpTimer = null;
$('msgs').addEventListener('touchstart', e => {
  const msg = e.target.closest('.msg'); if (!msg || msg.dataset.recalled === '1') return;
  const id = Number(msg.dataset.id), fromId = Number(msg.dataset.fromId);
  const mine = !!(ME && fromId === ME.id);
  if (mine) { if (Date.now() - Number(msg.dataset.createdAt || 0) > RECALL_MS) return; }
  else if (!e.target.closest('.msg .avatar') || !msg.dataset.fromName) return;
  clearTimeout(atLpTimer);
  atLpTimer = setTimeout(() => {
    const t = e.touches[0] || {};
    if (mine) showFloatMenu(t.clientX || 0, t.clientY || 0, [{ label: '撤回', cls: 'danger', fn: () => recallMsg(id) }]);
    else mentionByAvatar(fromId, msg.dataset.fromName);        // 直接 @，不弹菜单
    try { if (navigator.vibrate) navigator.vibrate(15); } catch (er) {}
  }, 500);
}, { passive: true });
$('msgs').addEventListener('touchmove', () => clearTimeout(atLpTimer), { passive: true });
$('msgs').addEventListener('touchend', () => clearTimeout(atLpTimer));
$('msgs').addEventListener('touchcancel', () => clearTimeout(atLpTimer));
/* ---- 「重新编辑」---- */
$('msgs').addEventListener('click', e => {
  const b = e.target.closest('.recall-edit'); if (!b) return;
  const msg = b.closest('.msg'); if (!msg) return;
  insertRecalledText(msg.dataset.content || '');
});

document.addEventListener('paste', e => {
  const items = e.clipboardData?.items || [];
  const inProfile = $('profileModal').classList.contains('on');
  for (const it of items) {
    if (it.kind === 'file') {
      const f = it.getAsFile();
      if (!f) continue;
      e.preventDefault();
      if (inProfile) { toast('🖼 正在上传头像：' + (f.name || '粘贴的图片')); uploadAvatar(f); }
      else { toast('📎 正在上传：' + (f.name || '粘贴的文件')); uploadAndSend(f); }
      break;
    }
  }
});

/* 滚动：更新“是否贴底”状态；滑到顶部附近则加载更早的消息 */
$('msgs').addEventListener('scroll', () => {
  const box = $('msgs');
  autoStick = (box.scrollHeight - box.scrollTop - box.clientHeight) < 120;
  if (box.scrollTop < 80 && hasMore && !loadingMore) loadMore();
});

/* 侧栏 */
function openSidebar() { $('sidebar').classList.add('open'); $('mask').classList.add('on'); }
function closeSidebar() { $('sidebar').classList.remove('open'); $('mask').classList.remove('on'); }

/* ---------------- 启动 ---------------- */
(async function boot() {
  try {
    loadInviteInfo();                    // 登录页也要知道是否需要邀请码
    const r = await fetch('/api/me');
    const d = await r.json();
    if (d.ok && d.user) { ME = d.user; enterApp(); }
  } catch (e) {}
})();
