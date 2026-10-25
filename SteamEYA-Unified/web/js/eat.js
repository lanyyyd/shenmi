/* =========================================================
   「今天吃点啥」前端逻辑
   - 纯原生 JS，无任何外部库 / CDN
   - 全部接口走相对路径（同源，自动带 cookie）
   - 渲染一律 esc 转义文本；图片仅放行 /uploads/ 或 https://
   ========================================================= */
'use strict';

/* ============================ 基础工具 ============================ */
var $ = function (s) { return document.querySelector(s); };
var $$ = function (s) { return Array.prototype.slice.call(document.querySelectorAll(s)); };

function esc(v) {
  return String(v == null ? '' : v).replace(/[&<>"']/g, function (c) {
    return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
  });
}

/* 安全过滤：只允许同源 /uploads/ 或 https:// 开头的图片地址 */
function safeUrl(u) {
  u = String(u || '');
  return (u.indexOf('/uploads/') === 0 || u.indexOf('https://') === 0) ? u : '';
}

function imgTag(url, alt, cls, status) {
  var s = safeUrl(url);
  if (!s) {
    var pending = (status === 'queued' || status === 'running');
    return '<div class="img-ph' + (pending ? ' img-pending' : '') + ' ' + (cls || '') + '"' +
      (pending ? ' data-ai-pending="1"' : '') + '>' +
      (pending ? '<span class="img-pending-txt">AI 配图中…</span>' : '') + '</div>';
  }
  return '<img src="' + esc(s) + '" alt="' + esc(alt || '') + '" loading="lazy" class="' + (cls || '') + '">';
}

function initials(name) {
  var n = String(name || '?').trim();
  return (n.charAt(0) || '?').toUpperCase();
}
var AV_COLORS = ['#FFB020', '#FF8A00', '#FFC53D', '#E06A00', '#FFA726', '#F0A34A'];
function avColor(id) {
  var s = String(id || ''), h = 0;
  for (var i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0;
  return AV_COLORS[h % AV_COLORS.length];
}
function avatarHtml(u) {
  u = u || {};
  var url = safeUrl(u.avatar);
  if (url) return '<img class="rv-av" src="' + esc(url) + '" alt="" loading="lazy">';
  return '<div class="rv-av rv-av-txt" style="background:' + avColor(u.id) + '">' + esc(initials(u.nickname || u.username)) + '</div>';
}

function starsHtml(n) {
  var s = '';
  for (var i = 1; i <= 5; i++) s += (i <= n ? '★' : '☆');
  return s;
}
function storeRatingHtml(r) {
  if (r === null || r === undefined || r === '') return '<span class="rating-none">暂无评分</span>';
  var v = Number(r);
  if (isNaN(v)) return '<span class="rating-none">暂无评分</span>';
  return '<span class="stars">' + starsHtml(Math.round(v)) + ' ' + v.toFixed(1) + '</span>';
}
function reviewStars(r) {
  if (r === null || r === undefined || r === '') return '';
  return '<span class="stars">' + starsHtml(Math.round(Number(r))) + '</span>';
}
function ratingText(r) {
  if (r === null || r === undefined || r === '') return '暂无评分';
  var v = Number(r);
  return isNaN(v) ? '暂无评分' : v.toFixed(1);
}
function money(v) {
  var n = Number(v || 0);
  return isNaN(n) ? '0' : (Math.round(n * 100) / 100);
}

function fmtTime(ms) {
  var n = Number(ms);
  if (!n) return '';
  var d = new Date(n), now = new Date(), diff = (now - d) / 1000;
  if (diff < 60) return '刚刚';
  if (diff < 3600) return Math.floor(diff / 60) + '分钟前';
  if (diff < 86400) return Math.floor(diff / 3600) + '小时前';
  if (diff < 86400 * 7) return Math.floor(diff / 86400) + '天前';
  return d.toLocaleDateString('zh-CN');
}

function toast(msg) {
  var t = $('#toast');
  t.textContent = msg;
  t.classList.add('show');
  clearTimeout(t._timer);
  t._timer = setTimeout(function () { t.classList.remove('show'); }, 1900);
}

/* ============================ 请求层 ============================ */
async function API(path, opt) {
  opt = opt || {};
  var init = { method: opt.method || 'GET', credentials: 'same-origin', headers: {} };
  if (opt.json !== undefined) {
    init.body = JSON.stringify(opt.json);
    init.headers['Content-Type'] = 'application/json';
  } else if (opt.body !== undefined) {
    init.body = opt.body;
  }
  if (opt.headers) {
    for (var k in opt.headers) init.headers[k] = opt.headers[k];
  }
  var res = await fetch(path, init);
  var data = {};
  var txt = '';
  try { txt = await res.text(); } catch (e) { txt = ''; }
  if (txt) { try { data = JSON.parse(txt); } catch (e) { data = {}; } }
  if (data && data.ok === false) {
    var e1 = new Error(data.error || '操作失败');
    e1.status = res.status;
    throw e1;
  }
  if (!res.ok) {
    var m = (data && (data.error || data.detail)) || ('请求失败 (' + res.status + ')');
    var e2 = new Error(typeof m === 'string' ? m : '请求失败');
    e2.status = res.status;
    throw e2;
  }
  return data;
}

/* 上传前压缩：转 webp、质量 0.7、最长边限制 1600px（等比）。失败/不支持则返回 null，回退原图。 */
function compressImage(file, maxSide, quality) {
  return new Promise(function (resolve) {
    try {
      if (!file || !/^image\//.test(file.type || '')) { resolve(null); return; }
      var reader = new FileReader();
      reader.onload = function () {
        var im = new Image();
        im.onload = function () {
          try {
            var w = im.naturalWidth || im.width;
            var h = im.naturalHeight || im.height;
            if (!w || !h) { resolve(null); return; }
            var scale = Math.min(1, maxSide / Math.max(w, h));
            var dw = Math.max(1, Math.round(w * scale));
            var dh = Math.max(1, Math.round(h * scale));
            var c = document.createElement('canvas');
            c.width = dw; c.height = dh;
            var g = c.getContext('2d');
            g.drawImage(im, 0, 0, dw, dh);
            c.toBlob(function (blob) {
              resolve((blob && /webp/.test(blob.type)) ? blob : null);
            }, 'image/webp', quality);
          } catch (e) { resolve(null); }
        };
        im.onerror = function () { resolve(null); };
        im.src = reader.result;
      };
      reader.onerror = function () { resolve(null); };
      reader.readAsDataURL(file);
    } catch (e) { resolve(null); }
  });
}

async function uploadImage(file) {
  var name = 'eat-' + Date.now() + '.webp';
  var blob = await compressImage(file, 1600, 0.7);
  var up = file;
  if (blob) {
    try { up = new File([blob], name, { type: 'image/webp' }); }
    catch (e) { up = blob; }
  } else {
    name = file.name || ('eat-' + Date.now());
  }
  var fd = new FormData();
  fd.append('file', up, name);
  var r = await API('/api/eat/upload', { method: 'POST', body: fd });
  return r.url || '';
}

/* ============================ 状态 ============================ */
var state = { me: null, users: [], stores: [], dishes: [], reviews: [], wall: [], task: null, overridden: false, period: 'month' };
var currentTab = 'home';
var currentStore = null;
var currentStoreDishes = [];
var spinning = false;
var wheelRotation = -Math.PI / 2;
var reviewDraft = { parentId: null, storeId: null, dishId: null, rating: 0, image: '', images: [] };
var RV_MAX_IMGS = 9;
var formSave = null;
var formData = {};
var infoPrimaryFn = null;
var infoSecondaryFn = null;

/* ============================ 登录 / 会话 ============================ */
function deviceId() {
  var d = null;
  try { d = localStorage.getItem('eat.device'); } catch (e) { d = null; }
  if (!d) {
    d = 'web-' + Math.random().toString(36).slice(2) + Date.now().toString(36);
    try { localStorage.setItem('eat.device', d); } catch (e) {}
  }
  return d;
}

function needLogin() {
  state.me = null;
  if (typeof stopImgPolling === 'function') stopImgPolling();
  $('#loginScreen').style.display = 'flex';
  $('#app').style.display = 'none';
}

function fail(e) {
  if (e && e.status === 401) { needLogin(); toast('登录已失效，请重新登录'); return; }
  toast((e && e.message) ? e.message : '操作失败');
}

function updateHeaderUser() {
  var el = $('#hdAvatar');
  if (state.me) {
    var url = safeUrl(state.me.avatar);
    el.innerHTML = url ? '<img src="' + esc(url) + '" alt="">' : esc(initials(state.me.nickname || state.me.username));
    el.title = state.me.nickname || '';
  } else {
    el.textContent = '🍜';
  }
}

async function init() {
  try {
    var r = await API('/api/me');
    state.me = (r && r.user) ? r.user : null;
  } catch (e) { state.me = null; }
  if (state.me) enterApp(); else needLogin();
}

function enterApp() {
  $('#loginScreen').style.display = 'none';
  $('#app').style.display = 'block';
  updateHeaderUser();
  boot();
}

async function doLogin() {
  var username = $('#loginUser').value.trim();
  var password = $('#loginPass').value;
  var errEl = $('#loginErr');
  errEl.textContent = '';
  if (!username || !password) { errEl.textContent = '请填写用户名和密码'; return; }
  var btn = $('#loginBtn');
  btn.disabled = true;
  try {
    var r = await API('/api/login', { method: 'POST', json: { username: username, password: password, deviceId: deviceId() } });
    state.me = r.user;
    $('#loginPass').value = '';
    enterApp();
  } catch (e) {
    errEl.textContent = (e && e.message) ? e.message : '登录失败';
  } finally {
    btn.disabled = false;
  }
}

$('#loginBtn').addEventListener('click', doLogin);
$('#loginPass').addEventListener('keydown', function (e) { if (e.key === 'Enter') doLogin(); });

$('#logoutBtn').addEventListener('click', async function () {
  try { await API('/api/logout', { method: 'POST' }); } catch (e) {}
  needLogin();
  toast('已退出登录');
});

/* ============================ 启动 / 数据加载 ============================ */
async function boot() {
  try {
    await loadStores();
    await loadDishes();
  } catch (e) { fail(e); }
  loadUsers().catch(function () {});
  showTab('home');
  await popTaskOnLoad();
}

async function loadUsers() {
  try {
    var r = await API('/api/eat/users');
    state.users = r.users || [];
  } catch (e) {
    state.users = [];
  }
}

async function popTaskOnLoad() {
  try {
    var r = await API('/api/eat/task');
    state.task = r.task || null;
    state.overridden = !!r.overridden;
    if (state.task) showTaskModal();
  } catch (e) { /* 未登录等情况忽略 */ }
}

async function loadStores() {
  var r = await API('/api/eat/stores');
  state.stores = r.stores || [];
  renderHome();
  if (hasPendingImages()) startImgPolling();
}
async function loadDishes() {
  var r = await API('/api/eat/dishes');
  state.dishes = r.dishes || [];
  drawWheel();
  if (hasPendingImages()) startImgPolling();
}

/* ---------- AI 配图：排队/生成中时轮询刷新 ---------- */
var imgPollTimer = null;
var imgPollTicks = 0;

function isImgPending(x) {
  return !!x && (x.imageStatus === 'queued' || x.imageStatus === 'running');
}
function hasPendingImages() {
  var i;
  for (i = 0; i < (state.stores || []).length; i++) if (isImgPending(state.stores[i])) return true;
  for (i = 0; i < (state.dishes || []).length; i++) if (isImgPending(state.dishes[i])) return true;
  if (isImgPending(currentStore)) return true;
  for (i = 0; i < (currentStoreDishes || []).length; i++) if (isImgPending(currentStoreDishes[i])) return true;
  return false;
}
function startImgPolling() {
  if (imgPollTimer) return;
  imgPollTicks = 0;
  imgPollTimer = setInterval(async function () {
    imgPollTicks++;
    try {
      await loadStores();
      await loadDishes();
      if (currentStore && $('#page-store').classList.contains('active')) await refreshStore();
    } catch (e) { /* 忽略：下一轮再试 */ }
    if (imgPollTicks >= 40 || !hasPendingImages()) stopImgPolling();
  }, 3000);
}
function stopImgPolling() {
  if (imgPollTimer) { clearInterval(imgPollTimer); imgPollTimer = null; }
}
async function refreshStore() {
  if (!currentStore) return;
  try {
    var r = await API('/api/eat/stores/' + encodeURIComponent(currentStore.id) + '/dishes');
    currentStore = r.store;
    currentStoreDishes = r.dishes || [];
    renderStore();
  } catch (e) { /* 忽略 */ }
}
async function loadReviews() {
  var r = await API('/api/eat/reviews?limit=50&offset=0');
  state.reviews = r.reviews || [];
  renderReviews();
  renderMyReviews();
}

/* ============================ 页面切换 ============================ */
/* 手机返回键：把「首页 → 店铺 → 菜品」的层级写进浏览器历史，返回时逐级回退 */
function navState() { return window.history.state || {}; }
function pushPage(st) { try { window.history.pushState(st, ''); } catch (e) {} }
function replacePage(st) { try { window.history.replaceState(st, ''); } catch (e) {} }
replacePage({ page: 'home' });   // 记录首页这一层，店铺/菜品再往上叠

function activatePage(name) {
  $$('.page').forEach(function (p) { p.classList.remove('active'); });
  var el = $('#page-' + name);
  if (el) el.classList.add('active');
  window.scrollTo(0, 0);
}

function showTab(name, fromPop) {
  currentTab = name;
  activatePage(name);
  $$('#tabbar .tab').forEach(function (t) { t.classList.toggle('active', t.dataset.tab === name); });
  $('#storeAction').style.display = 'none';
  if (name === 'wheel') drawWheel();
  if (name === 'review') loadReviews().catch(fail);
  if (name === 'mine') loadMine().catch(fail);
  if (!fromPop) replacePage({ page: name });
}

$$('#tabbar .tab').forEach(function (t) {
  t.addEventListener('click', function () { showTab(t.dataset.tab); });
});

/* ============================ 首页：店铺卡片流 ============================ */
function renderHome() {
  var box = $('#storeList');
  if (!state.stores.length) {
    box.innerHTML = '<div class="empty">还没有店铺，去「我的」页添加一家吧～</div>';
    return;
  }
  box.innerHTML = state.stores.map(function (s) {
    var rec = s.recommend || [];
    var recText = rec.length
      ? rec.map(function (d) { return esc(d.name) + ' ¥' + money(d.price); }).join(' · ')
      : '暂无推荐';
    return '<div class="card store-card" data-store="' + s.id + '">' +
      imgTag(s.image, s.name, 'store-thumb', s.imageStatus) +
      '<div class="body">' +
        '<div class="row1"><div class="name">' + esc(s.name) + '</div><div>' + storeRatingHtml(s.rating) + '</div></div>' +
        '<div class="loc">📍 ' + esc(s.location || '') + '</div>' +
        '<div class="rec">🍴 推荐：' + recText + '</div>' +
      '</div>' +
    '</div>';
  }).join('');
}

$('#storeList').addEventListener('click', function (e) {
  var c = e.target.closest('.store-card');
  if (c) openStore(c.dataset.store);
});

/* ============================ 店铺详情 ============================ */
function canEditStore(s) { return !!(state.me && (state.me.isAdmin || s.ownerId === state.me.id)); }
function canEditDish(d) { return !!(state.me && (state.me.isAdmin || d.ownerId === state.me.id)); }

async function openStore(id, fromPop) {
  try {
    var r = await API('/api/eat/stores/' + encodeURIComponent(id) + '/dishes');
    currentStore = r.store;
    currentStoreDishes = r.dishes || [];
    renderStore();
    activatePage('store');
    $$('#tabbar .tab').forEach(function (t) { t.classList.toggle('active', t.dataset.tab === 'home'); });
    $('#storeAction').style.display = 'block';
    var st = navState();
    if (!fromPop && !(st.page === 'store' && String(st.storeId) === String(id))) {
      pushPage({ page: 'store', storeId: String(id) });
    }
    if (hasPendingImages()) startImgPolling();
  } catch (e) { fail(e); }
}

function renderStore() {
  var s = currentStore;
  if (!s) return;
  $('#storeHero').innerHTML =
    imgTag(s.image, s.name, 'hero-img', s.imageStatus) +
    '<div class="hero-info"><h2>' + esc(s.name) + '</h2>' +
    '<p>' + storeRatingHtml(s.rating) + (s.ratingCount ? '（' + s.ratingCount + ' 条）' : '') + ' · 📍 ' + esc(s.location || '') + '</p></div>';

  var acts = $('#storeActions');
  if (canEditStore(s)) {
    acts.style.display = 'flex';
    acts.innerHTML =
      '<button class="btn btn-soft btn-sm" data-edit-store>✏️ 编辑店铺</button>' +
      '<button class="btn btn-soft btn-sm" data-del-store>🗑️ 删除店铺</button>';
  } else {
    acts.style.display = 'none';
    acts.innerHTML = '';
  }

  var list = $('#dishList');
  var dishesHtml = currentStoreDishes.length
    ? currentStoreDishes.map(function (d) {
        var editable = canEditDish(d);
        return '<div class="dish" data-dish="' + d.id + '">' + imgTag(d.image, d.name, 'dish-img', d.imageStatus) +
          '<div class="d-info"><div class="d-name">' + esc(d.name) + '</div>' +
          '<div class="d-price">¥' + money(d.price) + ' <small>· 吃过 ' + Number(d.eatCount || 0) + ' 次</small></div></div>' +
          (editable
            ? '<div class="d-acts"><button class="icon-btn" data-edit-dish="' + d.id + '" title="编辑">✏️</button>' +
              '<button class="icon-btn" data-del-dish="' + d.id + '" title="删除">🗑️</button></div>'
            : '') +
        '</div>';
      }).join('')
    : '<div class="empty">这家店还没有菜品，点下面按钮添加吧～</div>';
  // 最后一个菜品下面：添加菜品按钮，且自动选中本店
  list.innerHTML = dishesHtml +
    '<button class="btn btn-soft store-add-dish" id="storeAddDishBtn">＋ 添加菜品</button>';
}

$('#backToList').addEventListener('click', function () {
  var st = navState();
  if (st.page === 'store' || st.page === 'dish') { window.history.back(); return; }
  showTab('home');
});

$('#storeActions').addEventListener('click', function (e) {
  if (e.target.closest('[data-edit-store]')) editStore();
  else if (e.target.closest('[data-del-store]')) deleteStore();
});
$('#dishList').addEventListener('click', function (e) {
  var ed = e.target.closest('[data-edit-dish]');
  if (ed) { editDish(ed.dataset.editDish); return; }
  var dl = e.target.closest('[data-del-dish]');
  if (dl) { deleteDish(dl.dataset.delDish); return; }
  if (e.target.closest('#storeAddDishBtn')) { openDishForm(currentStore ? currentStore.id : null); return; }
  var dish = e.target.closest('[data-dish]');
  if (dish) { openDishDetail(dish.dataset.dish); return; }
});

/* ---------- 餐品详情（点开查看 + 「就吃这个了！」） ---------- */
var dishDetail = null;

function openDishDetail(id, fromPop) {
  var d = null;
  for (var i = 0; i < currentStoreDishes.length; i++) {
    if (String(currentStoreDishes[i].id) === String(id)) { d = currentStoreDishes[i]; break; }
  }
  if (!d) { toast('菜品不存在'); return; }
  dishDetail = d;
  var s = currentStore || {};
  $('#dishDetail').innerHTML =
    imgTag(d.image, d.name, 'dd-img', d.imageStatus) +
    '<div class="dd-name">' + esc(d.name) + '</div>' +
    '<div class="dd-meta">' +
      '🍽️ ' + esc(s.name || '') + '<br>' +
      '📍 ' + esc(s.location || '') + '<br>' +
      '价格 ¥' + money(d.price) + '<br>' +
      '⭐ ' + ratingText(s.rating) + (s.ratingCount ? '（' + Number(s.ratingCount) + ' 条评价）' : '') + '<br>' +
      '被吃过 ' + Number(d.eatCount || 0) + ' 次' +
    '</div>';
  $('#dishMask').classList.add('show');
  if (!fromPop) pushPage({ page: 'dish', dishId: String(id) });
}

function closeDishDetail(noHistory) {
  if (!noHistory) {
    var st = navState();
    if (st.page === 'dish') { window.history.back(); return; }
  }
  $('#dishMask').classList.remove('show');
}

window.addEventListener('popstate', function (e) {
  var st = e.state || { page: 'home' };
  if (st.page === 'dish') { return; }          // 前进回到菜品时保持当前弹层
  $('#dishMask').classList.remove('show');
  if (st.page === 'store') {
    if (!currentStore || String(currentStore.id) !== String(st.storeId)) {
      openStore(st.storeId, true);
    } else {
      activatePage('store');
      $$('#tabbar .tab').forEach(function (t) { t.classList.toggle('active', t.dataset.tab === 'home'); });
      $('#storeAction').style.display = 'block';
    }
    return;
  }
  showTab(st.page || 'home', true);
});

async function eatThisDish() {
  if (!state.me) { needLogin(); toast('请先登录'); return; }
  if (!dishDetail) return;
  try {
    var r = await API('/api/eat/choose', { method: 'POST', json: { dishId: Number(dishDetail.id) } });
    state.task = r.task || null;
    state.overridden = !r.global;
    closeDishDetail();
    toast('已生成任务，快去吃吧！');
  } catch (e) { fail(e); }
}
$('#eatThisDishBtn').addEventListener('click', eatThisDish);
$('#dishCancel').addEventListener('click', function () { closeDishDetail(); });
$('#dishMask').addEventListener('click', function (e) { if (e.target === this) closeDishDetail(); });

/* 「就吃这家了！」 */
$('#storeAction').addEventListener('click', pickFromStore);

async function pickFromStore() {
  if (!state.me) { needLogin(); toast('请先登录'); return; }
  if (!currentStore) return;
  if (!currentStoreDishes.length) { toast('这家店还没有菜品哦～'); return; }
  try {
    var r = await API('/api/eat/pick', { method: 'POST', json: { storeId: Number(currentStore.id) } });
    state.task = r.task || null;
    state.overridden = !r.global;
    showConfirmTask(r.task);
  } catch (e) { fail(e); }
}

/* ============================ 通用信息 / 确认弹窗 ============================ */
function showInfo(opts) {
  opts = opts || {};
  $('#infoTitle').textContent = opts.title || '';
  $('#infoSub').textContent = opts.sub || '';
  $('#infoSub').style.display = opts.sub ? 'block' : 'none';
  $('#infoBody').innerHTML = opts.body || '';
  $('#infoPrimary').textContent = opts.primaryText || '确定';
  $('#infoPrimary').style.display = (opts.primaryText === null) ? 'none' : 'inline-flex';
  $('#infoSecondary').style.display = (opts.secondaryText === null) ? 'none' : 'inline-flex';
  $('#infoSecondary').textContent = opts.secondaryText || (opts.primaryText === null ? '关闭' : '取消');
  infoPrimaryFn = opts.onPrimary || null;
  infoSecondaryFn = opts.onSecondary || null;
  $('#infoMask').classList.add('show');
}
$('#infoPrimary').addEventListener('click', function () {
  $('#infoMask').classList.remove('show');
  if (infoPrimaryFn) infoPrimaryFn();
});
$('#infoSecondary').addEventListener('click', function () {
  $('#infoMask').classList.remove('show');
  if (infoSecondaryFn) infoSecondaryFn();
});

function taskBoxHtml(task) {
  return '<div class="task-dish-box">' +
    '<div class="tname">' + esc(task.dishName) + '</div>' +
    '<div class="tmeta">🍽️ ' + esc(task.storeName) + '<br>📍 ' + esc(task.location || '') +
    '<br>价格 ¥' + money(task.price) + '<br>⭐ ' + ratingText(task.rating) + '</div></div>';
}
function showConfirmTask(task) {
  if (!task) return;
  showInfo({
    title: '🍽️ 就决定是你了',
    sub: '帮你从这家店挑了一个～',
    body: taskBoxHtml(task),
    primaryText: '就吃这个了！',
    onPrimary: function () { toast('已生成任务，快去吃吧！'); },
    secondaryText: '再想想'
  });
}

/* ============================ 转盘 ============================ */
var WHEEL_COLORS = ['#FFB020', '#FF8A00', '#FFC53D', '#FFD97A', '#FF9E2C', '#FFB84D'];

function wheelItems() {
  return state.dishes.map(function (d) {
    return { id: d.id, name: d.name, price: d.price, storeId: d.storeId, storeName: d.storeName || '', location: d.location || '' };
  });
}

function drawWheel() {
  var c = $('#wheelCanvas');
  if (!c) return;
  var ctx = c.getContext('2d');
  var items = wheelItems();
  var R = c.width / 2;
  ctx.clearRect(0, 0, c.width, c.height);
  var emptyEl = $('#wheelEmpty');
  if (!items.length) { if (emptyEl) emptyEl.style.display = 'block'; return; }
  if (emptyEl) emptyEl.style.display = 'none';

  var seg = (Math.PI * 2) / items.length;
  for (var i = 0; i < items.length; i++) {
    var a0 = i * seg + wheelRotation;
    ctx.beginPath();
    ctx.moveTo(R, R);
    ctx.arc(R, R, R - 16, a0, a0 + seg);
    ctx.closePath();
    ctx.fillStyle = WHEEL_COLORS[i % WHEEL_COLORS.length];
    ctx.fill();
    ctx.strokeStyle = '#FFFFFF';
    ctx.lineWidth = 3;
    ctx.stroke();

    ctx.save();
    ctx.translate(R, R);
    ctx.rotate(a0 + seg / 2);
    ctx.textAlign = 'right';
    ctx.textBaseline = 'middle';
    ctx.fillStyle = '#FFFFFF';
    ctx.font = '700 22px system-ui,"PingFang SC",sans-serif';
    var label = items[i].name + '·' + items[i].storeName;
    if (label.length > 10) label = label.slice(0, 10) + '…';
    ctx.fillText(label, R - 30, 0);
    ctx.restore();
  }

  ctx.beginPath();
  ctx.arc(R, R, 72, 0, Math.PI * 2);
  ctx.fillStyle = '#FFFFFF';
  ctx.fill();
  ctx.strokeStyle = '#FF8A00';
  ctx.lineWidth = 10;
  ctx.stroke();
  ctx.fillStyle = '#E06A00';
  ctx.font = '900 26px system-ui';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText('抽', R, R);
}

async function doSpin() {
  if (spinning) return;
  if (!state.me) { needLogin(); toast('请先登录'); return; }
  var items = wheelItems();
  if (!items.length) { toast('还没有菜品可以抽～'); return; }
  try {
    var r = await API('/api/eat/spin', { method: 'POST' });
    state.task = r.task || null;
    state.overridden = !r.global;
    var t = r.task;
    var idx = items.findIndex(function (x) { return x.id === t.dishId && x.storeId === t.storeId; });
    if (idx < 0) idx = items.findIndex(function (x) { return x.id === t.dishId; });
    if (idx < 0) idx = Math.floor(Math.random() * items.length);
    spinToIndex(idx, items.length, function () { showResult(t); });
  } catch (e) { fail(e); }
}

function spinToIndex(idx, total, done) {
  spinning = true;
  $('#spinBtn').disabled = true;
  $('#resultArea').innerHTML = '';
  var seg = (Math.PI * 2) / total;
  var base = -(idx + 0.5) * seg;
  var cur = ((wheelRotation % (Math.PI * 2)) + Math.PI * 2) % (Math.PI * 2);
  var delta = ((base - cur) + Math.PI * 2) % (Math.PI * 2) + Math.PI * 2 * 5;
  var start = wheelRotation, target = wheelRotation + delta;
  var t0 = performance.now(), dur = 3600;
  function frame(now) {
    var p = Math.min(1, (now - t0) / dur);
    var ease = 1 - Math.pow(1 - p, 3);
    wheelRotation = start + (target - start) * ease;
    drawWheel();
    if (p < 1) requestAnimationFrame(frame);
    else {
      spinning = false;
      $('#spinBtn').disabled = false;
      if (done) done();
    }
  }
  requestAnimationFrame(frame);
}

function showResult(task) {
  if (!task) return;
  $('#resultArea').innerHTML = '<div class="result-card">' +
    '<div class="big">🎉 抽到了：<em>' + esc(task.dishName) + '</em></div>' +
    '<div class="line">🍽️ ' + esc(task.storeName) + ' · ¥' + money(task.price) + '</div>' +
    '<div class="line">📍 ' + esc(task.location || '') + '</div>' +
    '<div class="line">⭐ ' + ratingText(task.rating) + '</div>' +
    '<button class="btn btn-primary" id="resultEatBtn" style="margin-top:14px;">就吃这个了！</button>' +
  '</div>';
  $('#resultEatBtn').addEventListener('click', function () { toast('已生成任务，快去吃吧！'); });
}

$('#spinBtn').addEventListener('click', doSpin);

$('#againBtn').addEventListener('click', async function () {
  if (spinning) return;
  if (!state.me) { needLogin(); toast('请先登录'); return; }
  try {
    await API('/api/eat/task/other', { method: 'POST' });
    state.overridden = true;
    $('#resultArea').innerHTML = '<div class="tip-note">🔄 已重置你自己的转盘，之后抽取只对自己生效。</div>';
    toast('已重置你自己的转盘，之后抽取只对自己生效');
  } catch (e) { fail(e); }
});

/* ============================ 评价 feed ============================ */
function reviewCardHtml(r, isSame) {
  var u = r.user || {};
  var imgs = (r.images && r.images.length) ? r.images : (r.image ? [r.image] : []);
  var imgsHtml = imgs.length
    ? '<div class="review-imgs' + (imgs.length === 1 ? ' one' : '') + '">' + imgs.map(function (url) {
        var s = safeUrl(url);
        if (!s) return '';
        return '<img class="rv-img-cell" data-view="' + esc(s) + '" src="' + esc(s) + '" loading="lazy" alt="">';
      }).join('') + '</div>'
    : '';
  var sames = (!isSame && r.sames && r.sames.length)
    ? '<div class="same-list">' + r.sames.map(function (k) { return reviewCardHtml(k, true); }).join('') + '</div>'
    : '';
  return '<div class="review-card' + (isSame ? ' same' : '') + '" data-rid="' + r.id + '">' +
    '<div class="rv-head">' + avatarHtml(u) +
      '<div class="rv-author"><div class="rv-name">' + esc(u.nickname || '匿名用户') + '</div>' +
      '<div class="rv-sub">' + esc(r.storeName || '') + (r.dishName ? ' · ' + esc(r.dishName) : '') + '</div></div>' +
      '<div class="rv-time">' + fmtTime(r.createdAt) + '</div>' +
    '</div>' +
    (r.rating ? '<div class="rv-stars">' + reviewStars(r.rating) + '</div>' : '') +
    (r.comment ? '<div class="rv-comment">' + esc(r.comment) + '</div>' : '') +
    (r.guests && r.guests.length
      ? '<div class="rv-guests">🍽️ 一起吃：' + r.guests.map(function (g) { return esc(g.nickname || g.username || ''); }).join('、') + '</div>'
      : '') +
    imgsHtml +
    '<div class="rv-actions">' +
      '<button class="rv-act like' + (r.liked ? ' on' : '') + '" data-like="' + r.id + '">' + (r.liked ? '♥' : '♡') + ' <span>' + Number(r.likeCount || 0) + '</span></button>' +
      (isSame ? '' : '<button class="rv-act same-btn" data-same="' + r.id + '">💬 同评 <span>' + Number(r.sameCount || 0) + '</span></button>') +
      (canEditReview(r) ? '<button class="rv-act edit" data-edit-review="' + r.id + '" title="编辑评价">✏️</button>' : '') +
      (canDelReview(r) ? '<button class="rv-act del" data-del-review="' + r.id + '" title="删除评价">🗑️</button>' : '') +
    '</div>' +
    sames +
  '</div>';
}

function renderReviews() {
  var box = $('#reviewFeed');
  if (!state.reviews.length) {
    box.innerHTML = '<div class="empty">还没有评价，快来发第一条吧～</div>';
    return;
  }
  box.innerHTML = state.reviews.map(function (r) { return reviewCardHtml(r, false); }).join('');
}

function findReview(id) {
  id = String(id);
  for (var i = 0; i < state.reviews.length; i++) {
    var r = state.reviews[i];
    if (String(r.id) === id) return r;
    if (r.sames) {
      for (var j = 0; j < r.sames.length; j++) {
        if (String(r.sames[j].id) === id) return r.sames[j];
      }
    }
  }
  return null;
}

async function toggleLike(btn) {
  if (!state.me) { needLogin(); toast('请先登录'); return; }
  var id = btn.dataset.like;
  try {
    var r = await API('/api/eat/reviews/' + encodeURIComponent(id) + '/like', { method: 'POST' });
    btn.classList.toggle('on', r.liked);
    btn.innerHTML = (r.liked ? '♥' : '♡') + ' <span>' + Number(r.likeCount || 0) + '</span>';
    var rec = findReview(id);
    if (rec) { rec.liked = r.liked; rec.likeCount = r.likeCount; }
  } catch (e) { fail(e); }
}

function canDelReview(r) {
  return !!(state.me && r && r.user && (state.me.isAdmin || String(r.user.id) === String(state.me.id)));
}

function canEditReview(r) {
  return !!(state.me && r && r.user && String(r.user.id) === String(state.me.id));
}

function editReview(id) {
  var r = findReview(id);
  if (!r) { toast('评价不存在'); return; }
  if (!canEditReview(r)) { toast('只能编辑自己发布的评价'); return; }
  openReviewEditor({
    editId: r.id,
    parentId: r.parentId || null,
    storeId: r.storeId, dishId: r.dishId,
    storeName: r.storeName, dishName: r.dishName,
    rating: r.rating, comment: r.comment, image: r.image, images: r.images,
    guestIds: (r.guests || []).map(function (g) { return g.id; })
  });
}

async function deleteReview(id) {
  if (!state.me) { needLogin(); toast('请先登录'); return; }
  var r = findReview(id);
  var sameN = (r && r.sames && r.sames.length) ? r.sames.length : 0;
  showInfo({
    title: '删除评价',
    sub: sameN
      ? '这条评价下有 ' + sameN + ' 条同评：删除后第一条同评会自动升为主评，其余同评跟随过来。'
      : '删除后，这条消费记录会从店铺评分、我的评价、月度/年度总结和图片墙中一并去掉。',
    body: r ? '<div class="target-box"><b>' + esc(r.dishName || '评价') + '</b><span>' + esc(r.comment || '（没有写评语）') + '</span></div>' : '',
    primaryText: '确认删除',
    onPrimary: async function () {
      try {
        await API('/api/eat/reviews/' + encodeURIComponent(id), { method: 'DELETE' });
        toast('评价已删除');
        await loadReviews();
        loadStores().catch(function () {});
        if (currentTab === 'mine') loadMine().catch(fail);
      } catch (e) { fail(e); }
    },
    secondaryText: '取消'
  });
}

$('#reviewFeed').addEventListener('click', function (e) {
  var del = e.target.closest('[data-del-review]');
  if (del) { deleteReview(del.dataset.delReview); return; }
  var ed = e.target.closest('[data-edit-review]');
  if (ed) { editReview(ed.dataset.editReview); return; }
  var like = e.target.closest('[data-like]');
  if (like) { toggleLike(like); return; }
  var same = e.target.closest('[data-same]');
  if (same) { openSameEditor(same.dataset.same); return; }
  var img = e.target.closest('[data-view]');
  if (img) { openViewer(img.dataset.view); return; }
});

function openSameEditor(pid) {
  var r = findReview(pid);
  if (!r) { toast('评价不存在'); return; }
  openReviewEditor({
    parentId: r.id, storeId: r.storeId, dishId: r.dishId,
    storeName: r.storeName, dishName: r.dishName
  });
}

$('#newReviewBtn').addEventListener('click', function () {
  if (!state.me) { needLogin(); toast('请先登录'); return; }
  openReviewEditor({});
});

/* ============================ 评价 / 同评编辑器 ============================ */
function fillDishSelect(storeId) {
  var list = state.dishes.filter(function (d) { return String(d.storeId) === String(storeId); });
  $('#rvDish').innerHTML = list.length
    ? list.map(function (d) { return '<option value="' + d.id + '">' + esc(d.name) + '（¥' + money(d.price) + '）</option>'; }).join('')
    : '<option value="">（该店暂无菜品）</option>';
}

function renderGuestPicker(selected) {
  var box = $('#rvGuests');
  if (!box) return;
  var sel = {};
  (selected || []).forEach(function (x) { sel[String(x)] = 1; });
  var list = (state.users || []).filter(function (u) {
    return !state.me || String(u.id) !== String(state.me.id);
  });
  box.innerHTML = list.length
    ? list.map(function (u) {
        var nick = u.nickname || u.username || ('用户' + u.id);
        return '<label class="guest-chip"><input type="checkbox" value="' + esc(u.id) + '"' +
          (sel[String(u.id)] ? ' checked' : '') + '><span>' + esc(nick) + '</span></label>';
      }).join('')
    : '<div class="guest-empty">暂无其他用户</div>';
}

function openReviewEditor(opts) {
  opts = opts || {};
  var editing = opts.editId != null;
  var imgs = [];
  if (opts.images && opts.images.length) imgs = opts.images.slice(0, RV_MAX_IMGS);
  else if (opts.image) imgs = [opts.image];
  reviewDraft = {
    editId: editing ? opts.editId : null,
    parentId: opts.parentId || null,
    storeId: opts.storeId != null ? opts.storeId : null,
    dishId: opts.dishId != null ? opts.dishId : null,
    rating: Number(opts.rating || 0),
    image: imgs[0] || '',
    images: imgs
  };

  $('#reviewTitle').textContent = editing
    ? (opts.parentId ? '✏️ 编辑同评' : '✏️ 编辑评价')
    : (opts.parentId ? '💬 同评' : '✍️ 发评价');

  var hasTarget = (reviewDraft.storeId != null || reviewDraft.dishId != null);
  if (hasTarget) {
    var st = null, dh = null;
    for (var i = 0; i < state.stores.length; i++) if (String(state.stores[i].id) === String(reviewDraft.storeId)) st = state.stores[i];
    for (var j = 0; j < state.dishes.length; j++) if (String(state.dishes[j].id) === String(reviewDraft.dishId)) dh = state.dishes[j];
    var sName = (st && st.name) || opts.storeName || '';
    var dName = (dh && dh.name) || opts.dishName || '';
    $('#reviewTarget').style.display = 'block';
    $('#reviewTarget').innerHTML = '<b>' + esc(dName || '菜品') + '</b><span>🍽️ ' + esc(sName || '') + (dh ? ' · ¥' + money(dh.price) : '') + '</span>';
    $('#reviewPick').style.display = 'none';
  } else {
    $('#reviewTarget').style.display = 'none';
    $('#reviewPick').style.display = 'block';
    $('#rvStore').innerHTML = state.stores.map(function (s) { return '<option value="' + s.id + '">' + esc(s.name) + '</option>'; }).join('');
    fillDishSelect($('#rvStore').value);
  }

  $('#reviewSub').textContent = editing
    ? '修改评分、评语或图片后保存'
    : (opts.parentId
      ? '同意这道菜？说说你的看法～打分、评语、图片都可留空'
      : '说说这一顿吃得怎么样～打分、评语、图片都可以留空');

  // 同评没有「一起吃」，只有主评（新建或编辑）才显示
  $('#reviewGuestsField').style.display = opts.parentId ? 'none' : 'block';

  updateReviewStars();
  renderGuestPicker(editing ? opts.guestIds : []);
  $('#rvComment').value = opts.comment || '';
  $('#rvFile').value = '';
  renderReviewImgs();
  $('#rvSubmit').textContent = editing ? '保存修改' : '提交';
  $('#reviewMask').classList.add('show');
}

function updateReviewStars() {
  $$('#rvStars span').forEach(function (s) {
    s.classList.toggle('on', Number(s.dataset.v) <= reviewDraft.rating);
  });
}
$$('#rvStars span').forEach(function (s) {
  s.addEventListener('click', function () {
    reviewDraft.rating = Number(s.dataset.v);
    updateReviewStars();
  });
});

$('#rvStore').addEventListener('change', function () { fillDishSelect($('#rvStore').value); });

function renderReviewImgs() {
  var box = $('#rvPreview');
  if (!box) return;
  var list = reviewDraft.images || [];
  box.innerHTML = list.map(function (url, i) {
    return '<div class="rv-thumb">' + imgTag(safeUrl(url), '', 'rv-thumb-img') +
      '<button type="button" class="rm" data-rm-thumb="' + i + '" title="移除">×</button></div>';
  }).join('');
}

$('#rvPreview').addEventListener('click', function (e) {
  var rm = e.target.closest('[data-rm-thumb]');
  if (rm) {
    reviewDraft.images.splice(Number(rm.dataset.rmThumb), 1);
    reviewDraft.image = reviewDraft.images[0] || '';
    renderReviewImgs();
    return;
  }
  var im = e.target.closest('.rv-thumb-img');
  if (im && safeUrl(im.getAttribute('src'))) openViewer(im.getAttribute('src'));
});

$('#rvFile').addEventListener('change', async function (e) {
  var files = e.target.files ? Array.prototype.slice.call(e.target.files) : [];
  e.target.value = '';
  if (!files.length) return;
  var over = false;
  for (var i = 0; i < files.length; i++) {
    if (reviewDraft.images.length >= RV_MAX_IMGS) { over = true; break; }
    try {
      toast('图片上传中…');
      var url = await uploadImage(files[i]);
      if (url && reviewDraft.images.indexOf(url) < 0 && reviewDraft.images.length < RV_MAX_IMGS) {
        reviewDraft.images.push(url);
      }
    } catch (err) { fail(err); }
  }
  reviewDraft.image = reviewDraft.images[0] || '';
  renderReviewImgs();
  toast(over ? '最多 9 张图片' : '图片已上传');
});

async function submitReview() {
  var storeId = reviewDraft.storeId, dishId = reviewDraft.dishId;
  if (!reviewDraft.parentId && reviewDraft.dishId == null) {
    storeId = $('#rvStore').value ? Number($('#rvStore').value) : null;
    dishId = $('#rvDish').value ? Number($('#rvDish').value) : null;
    if (!dishId) { toast('请选择菜品'); return; }
  }
  var editing = reviewDraft.editId != null;
  var guestIds = $$('#rvGuests input:checked').map(function (x) { return Number(x.value); })
    .filter(function (x) { return !!x; });
  var images = reviewDraft.images || [];
  var payload = {
    rating: reviewDraft.rating || null,
    comment: $('#rvComment').value.trim(),
    image: images[0] || '',
    images: images
  };
  if (editing) {
    // 同评没有「一起吃」，只有主评才提交成员
    if (!reviewDraft.parentId) payload.guestIds = guestIds;
  } else {
    payload.parentId = reviewDraft.parentId;
    payload.storeId = storeId;
    payload.dishId = dishId;
    payload.guestIds = guestIds;
  }
  try {
    var r = editing
      ? await API('/api/eat/reviews/' + encodeURIComponent(reviewDraft.editId), { method: 'PUT', json: payload })
      : await API('/api/eat/reviews', { method: 'POST', json: payload });
    $('#reviewMask').classList.remove('show');
    if (editing) toast('评价已更新');
    else toast(reviewDraft.parentId
      ? (r.taskCompleted ? '跟评已发布，任务完成 ✅' : '跟评已发布')
      : '评价已发布');
    await loadReviews();
    if (currentTab === 'mine') loadMine().catch(fail);
  } catch (e) { fail(e); }
}
$('#rvSubmit').addEventListener('click', submitReview);
$('#rvCancel').addEventListener('click', function () { $('#reviewMask').classList.remove('show'); });

/* ============================ 我的 ============================ */
async function loadMine() {
  try {
    var t = await API('/api/eat/task');
    state.task = t.task || null;
    state.overridden = !!t.overridden;
  } catch (e) { if (e.status !== 401) fail(e); }
  renderMineTask();
  await Promise.all([
    loadSummary().catch(function (e) { if (e.status !== 401) fail(e); }),
    loadWall().catch(function (e) { if (e.status !== 401) fail(e); }),
    loadReviews().catch(function (e) { if (e.status !== 401) fail(e); })
  ]);
}

function renderMineTask() {
  var box = $('#mineTask');
  var t = state.task;
  if (!t) {
    box.innerHTML = '<div class="card" style="padding:18px;"><div class="empty" style="padding:0;">还没有任务，去「转盘」抽一个吧～</div></div>';
    return;
  }
  box.innerHTML = '<div class="task-card">' +
    '<h3>🎯 当前任务' + (t.global ? '（全局共享）' : '（我的）') + '</h3>' +
    '<div class="tk-dish">' + esc(t.dishName) + '</div>' +
    '<div class="tk-meta">🍽️ ' + esc(t.storeName) + '<br>📍 ' + esc(t.location || '') + ' · ¥' + money(t.price) + ' · ⭐ ' + ratingText(t.rating) + '</div>' +
    '<button class="btn btn-soft" id="mineReviewBtn" style="margin-top:14px;width:100%;">✍️ 我要评价</button>' +
  '</div>';
  $('#mineReviewBtn').addEventListener('click', function () {
    openReviewEditor({ storeId: t.storeId, dishId: t.dishId, storeName: t.storeName, dishName: t.dishName });
  });
}

async function loadSummary() {
  var r = await API('/api/eat/summary?period=' + state.period);
  $('#statCount').textContent = r.count || 0;
  $('#statSpend').textContent = '¥' + money(r.spend);
  $('#statTop').textContent = r.topDish || r.topStore || '—';
  $('#statTopLbl').textContent = r.topDish ? '最常吃的菜' : '最常吃的店';
  $('#statPeriod').textContent = state.period === 'year' ? '今年' : '本月';
}

$$('.seg-btn').forEach(function (b) {
  b.addEventListener('click', function () {
    state.period = b.dataset.period;
    $$('.seg-btn').forEach(function (x) { x.classList.toggle('on', x === b); });
    loadSummary().catch(function (e) { if (e.status !== 401) fail(e); });
  });
});

function renderMyReviews() {
  var box = $('#mineRecords');
  if (!state.me) { box.innerHTML = ''; return; }
  var mine = state.reviews.filter(function (r) {
    if (r.user && String(r.user.id) === String(state.me.id)) return true;
    return (r.guests || []).some(function (g) { return String(g.id) === String(state.me.id); });
  });
  if (!mine.length) { box.innerHTML = '<div class="empty">还没有评价，快去抽一个任务吧～</div>'; return; }
  box.innerHTML = mine.map(function (r) {
    return '<div class="rec-item">' + imgTag(r.dishImage, r.dishName, 'rec-img') +
      '<div class="r-body"><div class="r-top"><div>' +
        '<div class="r-name">' + esc(r.dishName || '菜品') + '</div>' +
        '<div class="r-store">🍽️ ' + esc(r.storeName || '') + '</div>' +
      '</div><div>' + reviewStars(r.rating) + '</div></div>' +
      (r.comment ? '<div class="r-note">' + esc(r.comment) + '</div>' : '') +
      '<div class="r-time">' + fmtTime(r.createdAt) + '</div>' +
    '</div></div>';
  }).join('');
}

async function loadWall() {
  var r = await API('/api/eat/wall');
  state.wall = r.images || [];
  renderWall();
}
function renderWall() {
  var box = $('#imgWall');
  var items = state.wall.filter(function (it) { return !!safeUrl(it.image); });
  if (!items.length) {
    box.innerHTML = '<div class="empty" style="grid-column:1/-1;">暂无打卡图片</div>';
    return;
  }
  box.innerHTML = items.map(function (it) {
    var u = safeUrl(it.image);
    return '<img class="wall-img" data-view="' + esc(u) + '" src="' + esc(u) + '" loading="lazy" alt="">';
  }).join('');
}
$('#imgWall').addEventListener('click', function (e) {
  var img = e.target.closest('[data-view]');
  if (img) openViewer(img.dataset.view);
});

/* ---- 导出大图：仅拼接同源 /uploads/ 图片（跨域图跳过，避免污染 canvas） ---- */
function loadImage(url) {
  return new Promise(function (resolve, reject) {
    var im = new Image();
    im.onload = function () { resolve(im); };
    im.onerror = function () { reject(new Error('load fail')); };
    im.src = url;
  });
}
async function exportWall() {
  var list = state.wall.map(function (x) { return x.image; })
    .filter(function (u) { return safeUrl(u).indexOf('/uploads/') === 0; });
  if (!list.length) { toast('没有可导出的图片（仅支持本地上传图）'); return; }
  toast('正在合成大图…');
  var loaded = await Promise.all(list.map(function (u) { return loadImage(u).catch(function () { return null; }); }));
  var imgs = loaded.filter(function (x) { return !!x; });
  if (!imgs.length) { toast('图片加载失败'); return; }

  var cols = 3, cell = 400, gap = 8;
  var rows = Math.ceil(imgs.length / cols);
  var W = cols * cell + (cols + 1) * gap;
  var H = rows * cell + (rows + 1) * gap;
  var c = document.createElement('canvas');
  c.width = W; c.height = H;
  var g = c.getContext('2d');
  g.fillStyle = '#FFFFFF';
  g.fillRect(0, 0, W, H);

  imgs.forEach(function (im, i) {
    var r = Math.floor(i / cols), col = i % cols;
    var x = gap + col * (cell + gap);
    var y = gap + r * (cell + gap);
    var sw = im.width, sh = im.height;
    var scale = Math.max(cell / sw, cell / sh);
    var dw = sw * scale, dh = sh * scale;
    var sx = x + (cell - dw) / 2, sy = y + (cell - dh) / 2;
    g.save();
    g.beginPath();
    g.rect(x, y, cell, cell);
    g.clip();
    g.drawImage(im, sx, sy, dw, dh);
    g.restore();
  });

  c.toBlob(function (blob) {
    if (!blob) { toast('导出失败'); return; }
    var url = URL.createObjectURL(blob);
    var a = document.createElement('a');
    a.href = url;
    a.download = 'eat-wall-' + Date.now() + '.png';
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(function () { URL.revokeObjectURL(url); }, 2000);
    toast('大图已导出');
  }, 'image/png');
}
$('#exportWallBtn').addEventListener('click', function () { exportWall().catch(fail); });

/* ============================ 通用表单弹窗 ============================ */
function openForm(title, sub, fields, onSave, ai) {
  formSave = onSave;
  formData = {};
  $('#formTitle').textContent = title;
  $('#formSub').textContent = sub || '';
  $('#formSub').style.display = sub ? 'block' : 'none';
  var html = fields.map(function (f) {
    if (f.type === 'select') {
      var opts = (f.options || []).map(function (o) {
        return '<option value="' + esc(o.value) + '"' + (String(o.value) === String(f.value) ? ' selected' : '') + '>' + esc(o.label) + '</option>';
      }).join('');
      return '<div class="field"><label>' + esc(f.label) + '</label><select data-k="' + f.key + '">' + opts + '</select></div>';
    }
    if (f.type === 'file') {
      return '<div class="field"><label>' + esc(f.label) + '</label>' +
        '<input type="file" accept="image/*" data-fk="' + f.key + '">' +
        '<img class="preview" data-pv="' + f.key + '" alt=""></div>';
    }
    if (f.type === 'number') {
      return '<div class="field"><label>' + esc(f.label) + '</label>' +
        '<input type="number" data-k="' + f.key + '" value="' + esc(f.value == null ? '' : f.value) + '" placeholder="' + esc(f.placeholder || '') + '"></div>';
    }
    return '<div class="field"><label>' + esc(f.label) + '</label>' +
      '<input type="text" data-k="' + f.key + '" value="' + esc(f.value == null ? '' : f.value) + '" placeholder="' + esc(f.placeholder || '') + '"></div>';
  }).join('');
  if (ai && ai.id != null) html += aiImageBoxHtml(ai);
  $('#formFields').innerHTML = html;
  fields.forEach(function (f) { formData[f.key] = f.value == null ? '' : f.value; });
  $('#formMask').classList.add('show');
}

/* ---------- AI 重新生图：原有提示词 + 用户补充词 ---------- */
function aiImageBoxHtml(ai) {
  var img = safeUrl(ai.image);
  return '<div class="ai-img-box" data-ai-kind="' + esc(ai.kind) + '" data-ai-id="' + esc(ai.id) + '">' +
    '<div class="ai-img-title">🖼️ AI 重新生图</div>' +
    '<div class="ai-img-hint">补充提示词会追加到原有提示词后面，点「重新生图」后台生成，完成后自动替换并删除旧图。</div>' +
    '<div class="ai-img-body">' +
      (img
        ? '<img class="ai-img-preview" src="' + esc(img) + '" alt="">'
        : '<div class="ai-img-preview ai-img-empty">暂无图片</div>') +
      '<div class="ai-img-side">' +
        '<textarea class="ai-img-prompt" rows="3" maxlength="300" placeholder="例如：加一杯冰可乐、背景换成木质餐桌、暖色调"></textarea>' +
        '<div class="ai-img-row"><button type="button" class="btn btn-soft btn-sm" data-ai-regen>重新生图</button>' +
        '<span class="ai-img-status"></span></div>' +
      '</div>' +
    '</div>' +
  '</div>';
}

async function pollAiImage(kind, id, box, btn, st) {
  var tries = 0;
  var timer = setInterval(async function () {
    tries++;
    try {
      var r = await API('/api/eat/image-status?kind=' + encodeURIComponent(kind) + '&id=' + encodeURIComponent(id));
      if (r.status === 'ok') {
        clearInterval(timer);
        btn.disabled = false;
        st.textContent = '✅ 已生成';
        var img = box.querySelector('.ai-img-preview');
        if (r.image) {
          if (img && img.tagName === 'IMG') {
            img.src = r.image + '?t=' + Date.now();
          } else if (img) {
            var ni = document.createElement('img');
            ni.className = 'ai-img-preview';
            ni.src = r.image + '?t=' + Date.now();
            img.replaceWith(ni);
          }
        }
        loadStores().catch(function () {});
        loadDishes().catch(function () {});
        if (kind === 'dish' && currentStore) refreshStore().catch(function () {});
        return;
      }
      if (r.status === 'failed') {
        clearInterval(timer);
        btn.disabled = false;
        st.textContent = '生成失败：' + (r.error || '未知错误');
        return;
      }
      if (tries >= 40) {
        clearInterval(timer);
        btn.disabled = false;
        st.textContent = '仍在生成，可关闭窗口稍后查看';
      }
    } catch (e) { /* 网络抖动继续轮询 */ }
  }, 3000);
}

async function regenImage(btn) {
  var box = btn.closest('.ai-img-box');
  if (!box) return;
  var kind = box.getAttribute('data-ai-kind');
  var id = box.getAttribute('data-ai-id');
  var extra = (box.querySelector('.ai-img-prompt').value || '').trim();
  var st = box.querySelector('.ai-img-status');
  btn.disabled = true;
  st.textContent = '提交中…';
  try {
    await API('/api/eat/' + (kind === 'store' ? 'stores' : 'dishes') + '/' +
      encodeURIComponent(id) + '/regen-image', { method: 'POST', json: { extraPrompt: extra } });
    st.textContent = 'AI 生成中…（约 30–60 秒）';
    pollAiImage(kind, id, box, btn, st);
  } catch (e) {
    st.textContent = (e && e.message) ? e.message : '提交失败';
    btn.disabled = false;
  }
}

$('#formFields').addEventListener('click', function (e) {
  var btn = e.target.closest('[data-ai-regen]');
  if (btn) regenImage(btn);
});

$('#formFields').addEventListener('change', async function (e) {
  var fk = e.target.getAttribute('data-fk');
  if (fk && e.target.files && e.target.files[0]) {
    try {
      toast('图片上传中…');
      var url = await uploadImage(e.target.files[0]);
      formData[fk] = url;
      var pv = $('[data-pv="' + fk + '"]');
      if (pv) { pv.src = url; pv.style.display = 'block'; }
      toast('图片已上传');
    } catch (err) { fail(err); }
    return;
  }
  var k = e.target.getAttribute('data-k');
  if (k) formData[k] = e.target.value;
});

$('#formSave').addEventListener('click', async function () {
  $$('#formFields [data-k]').forEach(function (el) { formData[el.getAttribute('data-k')] = el.value; });
  if (!formSave) return;
  $('#formSave').disabled = true;
  try {
    await formSave(formData);
    $('#formMask').classList.remove('show');
  } catch (e) { fail(e); }
  finally { $('#formSave').disabled = false; }
});
$('#formCancel').addEventListener('click', function () { $('#formMask').classList.remove('show'); });

/* ---- 添加店铺 ---- */
function openAddStore() {
  if (!state.me) { needLogin(); toast('请先登录'); return; }
  openForm('＋ 添加店铺', '填写店铺信息，把它加入附近好店', [
    { key: 'name', label: '店铺名称', type: 'text', placeholder: '比如：巷口小笼包' },
    { key: 'location', label: '位置', type: 'text', placeholder: '比如：新餐厅 2楼' },
    { key: 'image', label: '图片（可选，可留空）', type: 'file' }
  ], async function (fd) {
    if (!String(fd.name || '').trim()) throw new Error('请填写店铺名称');
    await API('/api/eat/stores', {
      method: 'POST',
      json: { name: String(fd.name).trim(), location: String(fd.location || '').trim(), image: fd.image || '' }
    });
    toast('✅ 店铺已添加');
    await loadStores();
  });
}
$('#addStoreBtn').addEventListener('click', openAddStore);
var uploadTip = $('#storeUploadTip');
if (uploadTip) uploadTip.addEventListener('click', openAddStore);

/* ---- 添加菜品（支持预选店铺：店铺详情页最后一个菜品下面的按钮会带上该店） ---- */
function openDishForm(presetStoreId) {
  if (!state.me) { needLogin(); toast('请先登录'); return; }
  if (!state.stores.length) { toast('先添加一家店铺吧～'); return; }
  var sid = (presetStoreId != null) ? presetStoreId : state.stores[0].id;
  openForm('＋ 添加菜品', '给店里加一道菜，它会出现在转盘上', [
    { key: 'storeId', label: '所属店铺', type: 'select', value: sid, options: state.stores.map(function (s) { return { value: s.id, label: s.name }; }) },
    { key: 'name', label: '菜品名称', type: 'text', placeholder: '比如：鲜肉小笼' },
    { key: 'price', label: '价格（元）', type: 'number', placeholder: '比如：18' },
    { key: 'image', label: '图片（可选，可留空）', type: 'file' }
  ], async function (fd) {
    if (!String(fd.name || '').trim()) throw new Error('请填写菜品名称');
    if (!(Number(fd.price) > 0)) throw new Error('请填写正确的价格');
    await API('/api/eat/dishes', {
      method: 'POST',
      json: { storeId: Number(fd.storeId), name: String(fd.name).trim(), price: Number(fd.price), image: fd.image || '' }
    });
    toast('✅ 菜品已添加');
    await loadDishes();
    if (currentStore && String(currentStore.id) === String(fd.storeId)) await openStore(currentStore.id);
  });
}
$('#addDishBtn').addEventListener('click', function () { openDishForm(null); });

/* ---- 编辑 / 删除店铺 ---- */
function editStore() {
  var s = currentStore;
  if (!s) return;
  openForm('编辑店铺', '修改店铺信息', [
    { key: 'name', label: '店铺名称', type: 'text', value: s.name },
    { key: 'location', label: '位置', type: 'text', value: s.location },
    { key: 'image', label: '更换图片（可选，留空不变）', type: 'file' }
  ], async function (fd) {
    if (!String(fd.name || '').trim()) throw new Error('请填写店铺名称');
    var payload = { name: String(fd.name).trim(), location: String(fd.location || '').trim() };
    if (fd.image) payload.image = fd.image;
    await API('/api/eat/stores/' + s.id, { method: 'PUT', json: payload });
    toast('已保存');
    await loadStores();
    await openStore(s.id);
  }, { kind: 'store', id: s.id, image: s.image });
}
function deleteStore() {
  var s = currentStore;
  if (!s) return;
  showInfo({
    title: '删除店铺',
    sub: '会一并删除该店所有菜品与评价，确定吗？',
    body: '<div class="target-box"><b>' + esc(s.name) + '</b></div>',
    primaryText: '确认删除',
    onPrimary: async function () {
      try {
        await API('/api/eat/stores/' + s.id, { method: 'DELETE' });
        toast('店铺已删除');
        await loadStores();
        await loadDishes();
        showTab('home');
      } catch (e) { fail(e); }
    },
    secondaryText: '取消'
  });
}

/* ---- 编辑 / 删除菜品 ---- */
function editDish(id) {
  var d = null;
  for (var i = 0; i < currentStoreDishes.length; i++) if (String(currentStoreDishes[i].id) === String(id)) d = currentStoreDishes[i];
  if (!d) return;
  openForm('编辑菜品', '修改菜品信息', [
    { key: 'name', label: '菜品名称', type: 'text', value: d.name },
    { key: 'price', label: '价格（元）', type: 'number', value: d.price },
    { key: 'image', label: '更换图片（可选，留空不变）', type: 'file' }
  ], async function (fd) {
    if (!String(fd.name || '').trim()) throw new Error('请填写菜品名称');
    var payload = { name: String(fd.name).trim(), price: Number(fd.price) };
    if (fd.image) payload.image = fd.image;
    await API('/api/eat/dishes/' + d.id, { method: 'PUT', json: payload });
    toast('已保存');
    await openStore(currentStore.id);
    await loadDishes();
  }, { kind: 'dish', id: d.id, image: d.image });
}
function deleteDish(id) {
  var d = null;
  for (var i = 0; i < currentStoreDishes.length; i++) if (String(currentStoreDishes[i].id) === String(id)) d = currentStoreDishes[i];
  if (!d) return;
  showInfo({
    title: '删除菜品',
    sub: '会同时删除该菜的评价，确定吗？',
    body: '<div class="target-box"><b>' + esc(d.name) + '</b></div>',
    primaryText: '确认删除',
    onPrimary: async function () {
      try {
        await API('/api/eat/dishes/' + d.id, { method: 'DELETE' });
        toast('菜品已删除');
        await loadDishes();
        await loadStores();
        await openStore(currentStore.id);
      } catch (e) { fail(e); }
    },
    secondaryText: '取消'
  });
}

/* ============================ 打开页面时的任务弹窗 ============================ */
function showTaskModal() {
  var t = state.task;
  if (!t) return;
  $('#taskBox').innerHTML =
    '<div class="tname">' + esc(t.dishName) + '</div>' +
    '<div class="tmeta">你要吃的是 ' + esc(t.dishName) +
    '<br>位置在 ' + esc(t.location || '') +
    '<br>价格是 ' + money(t.price) + ' 元' +
    '<br>评分 ' + ratingText(t.rating) + '</div>';
  $('#taskMask').classList.add('show');
}
$('#knowBtn').addEventListener('click', function () { $('#taskMask').classList.remove('show'); });
$('#goReviewBtn').addEventListener('click', function () {
  $('#taskMask').classList.remove('show');
  var t = state.task;
  if (t) openReviewEditor({ storeId: t.storeId, dishId: t.dishId, storeName: t.storeName, dishName: t.dishName });
  else openReviewEditor({});
});

/* ============================ 图片查看器 ============================ */
function openViewer(url) {
  url = safeUrl(url);
  if (!url) return;
  $('#viewerImg').src = url;
  $('#viewerMask').classList.add('show');
}
$('#viewerMask').addEventListener('click', function () { $('#viewerMask').classList.remove('show'); });

/* ============================ 启动 ============================ */
function setupCanvas() {
  var c = $('#wheelCanvas');
  if (c) { c.width = 600; c.height = 600; }
}

setupCanvas();
init();
