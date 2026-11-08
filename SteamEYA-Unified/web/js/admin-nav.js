/* 管理后台统一顶部导航（原生 JS，无依赖） */
(function () {
  'use strict';

  var NAV_ITEMS = [
    { label: '首页', href: '/admin.html', title: '管理首页 · 用户与聊天' },
    { label: 'Steam', href: '/admin-steam.html', title: 'Steam 账号管理' },
    { label: '云游戏', href: '/admin-cloud.html', title: '云游戏账号管理' },
    { label: '更新', href: '/admin-update.html', title: '客户端更新管理' },
    { label: '积分', href: '/admin-points.html', title: '积分管理' },
    { label: '等级', href: '/admin-level.html', title: '等级管理' },
    { label: '抽卡', href: '/admin-gacha.html', title: '抽卡管理' },
    { label: '题库', href: '/admin-qa.html', title: '搜题题库' },
    { label: 'AI', href: '/admin-ai.html', title: 'AI 设置 · 小火人' },
    { label: '生图', href: '/admin-ai-image.html', title: 'AI 生图设置' },
    { label: 'Tips', href: '/admin-tips.html', title: '导航 tips' },
    { label: '聊天室', href: '/', title: '返回聊天室' }
  ];

  var THEME_ICONS = { auto: '🌗 自动', light: '☀️ 浅色', dark: '🌙 深色' };
  var THEME_ORDER = ['auto', 'light', 'dark'];
  var THEME_KEY = 'chat_theme_mode';

  function injectStyle() {
    if (document.getElementById('admin-nav-style')) return;
    var style = document.createElement('style');
    style.id = 'admin-nav-style';
    style.textContent = [
      '.admin-nav{display:flex;align-items:center;justify-content:flex-end;gap:6px;flex-wrap:wrap;flex:1 1 auto;min-width:0}',
      '.admin-nav-link{display:inline-flex;align-items:center;gap:4px;padding:5px 9px;border-radius:8px;',
      'font-size:12px;font-weight:600;line-height:1.2;cursor:pointer;text-decoration:none;white-space:nowrap;',
      'background:var(--panel);border:1px solid var(--line);color:var(--text);transition:.15s;font-family:inherit}',
      '.admin-nav-link:hover{border-color:var(--primary);color:var(--primary)}',
      '.admin-nav-link.on{background:var(--primary-l);border-color:var(--primary);color:var(--primary);font-weight:700}',
      '.admin-nav-link.danger{color:#EF4444}',
      '.admin-nav-link.danger:hover{border-color:#EF4444;color:#EF4444}',
      '.admin-nav-link:disabled{opacity:.6;cursor:default}',
      '@media(max-width:900px){',
      '.top{flex-wrap:nowrap;overflow-x:auto;-webkit-overflow-scrolling:touch;padding-bottom:4px}',
      '.top>h1,.top>#whoami{flex-shrink:0}',
      '.admin-nav{flex-wrap:nowrap;justify-content:flex-start;margin-left:12px;flex-shrink:0}',
      '}'
    ].join('');
    document.head.appendChild(style);
  }

  function normalizePath(path) {
    var p = String(path || '/').split('?')[0].split('#')[0];
    p = p.replace(/\/+$/, '');
    return p || '/';
  }

  function isCurrentPage(href) {
    var here = normalizePath(location.pathname).replace(/\.html$/, '');
    var target = normalizePath(href).replace(/\.html$/, '');
    return here === target;
  }

  function resolveTheme(mode) {
    if (mode === 'light') return 'light';
    if (mode === 'dark') return 'dark';
    var h = new Date().getHours();
    return (h >= 6 && h < 18) ? 'light' : 'dark';
  }

  function readMode() {
    var mode = 'auto';
    try { mode = localStorage.getItem(THEME_KEY) || 'auto'; } catch (e) {}
    return THEME_ORDER.indexOf(mode) >= 0 ? mode : 'auto';
  }

  function applyFallbackTheme(mode) {
    document.documentElement.dataset.mode = mode;
    document.documentElement.dataset.theme = resolveTheme(mode);
    try { localStorage.setItem(THEME_KEY, mode); } catch (e) {}
  }

  function fallbackCycle() {
    var cur = document.documentElement.dataset.mode || readMode();
    var idx = THEME_ORDER.indexOf(cur);
    if (idx < 0) idx = 0;
    applyFallbackTheme(THEME_ORDER[(idx + 1) % THEME_ORDER.length]);
  }

  function currentMode() {
    var mode = document.documentElement.dataset.mode || readMode();
    return THEME_ORDER.indexOf(mode) >= 0 ? mode : 'auto';
  }

  function buildNav() {
    var top = document.querySelector('.top');
    if (!top) return;

    // 初始化主题 dataset（按 localStorage 设置一次）
    var initMode = readMode();
    document.documentElement.dataset.mode = initMode;
    document.documentElement.dataset.theme = resolveTheme(initMode);

    var h1 = top.querySelector('h1');
    if (!h1) {
      h1 = document.createElement('h1');
      top.insertBefore(h1, top.firstChild);
    }

    var who = top.querySelector('#whoami');
    if (who && who.parentNode) who.parentNode.removeChild(who);

    // 清掉除 h1 外的所有子元素（按钮 / spacer / 旧导航）
    Array.prototype.slice.call(top.children).forEach(function (el) {
      if (el !== h1) top.removeChild(el);
    });
    top.insertBefore(h1, top.firstChild);
    if (who) top.insertBefore(who, h1.nextSibling);

    var nav = document.createElement('nav');
    nav.className = 'admin-nav';

    NAV_ITEMS.forEach(function (item) {
      var a = document.createElement('a');
      a.className = 'admin-nav-link';
      a.href = item.href;
      a.textContent = item.label;
      if (item.title) a.title = item.title;
      if (isCurrentPage(item.href)) {
        a.className += ' on';
        a.setAttribute('aria-current', 'page');
      }
      nav.appendChild(a);
    });

    var themeBtn = document.createElement('button');
    themeBtn.type = 'button';
    themeBtn.className = 'admin-nav-link';
    themeBtn.id = 'themeBtn';
    themeBtn.textContent = THEME_ICONS[currentMode()];
    themeBtn.addEventListener('click', function () {
      if (typeof window.cycleTheme === 'function') {
        window.cycleTheme();
      } else {
        fallbackCycle();
      }
      themeBtn.textContent = THEME_ICONS[currentMode()];
    });
    nav.appendChild(themeBtn);

    var logoutBtn = document.createElement('button');
    logoutBtn.type = 'button';
    logoutBtn.className = 'admin-nav-link danger';
    logoutBtn.textContent = '退出登录';
    logoutBtn.addEventListener('click', function () {
      if (typeof window.logout === 'function') {
        window.logout();
      } else {
        fetch('/api/logout', { method: 'POST' }).then(function () {
          location.href = '/';
        }, function () {
          location.href = '/';
        });
      }
    });
    nav.appendChild(logoutBtn);

    top.appendChild(nav);
  }

  function init() {
    injectStyle();
    buildNav();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
