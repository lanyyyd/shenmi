package com.teatt.pocket.feature.cloud

import android.webkit.WebView
import kotlinx.coroutines.suspendCancellableCoroutine
import org.json.JSONObject
import org.json.JSONTokener
import kotlin.coroutines.resume

/**
 * 云游戏 WebView 共用的注入脚本。
 *
 * 这里只放「与业务无关、手动抓取和静默续期都要用」的代码：
 * combo_token 钩子、读取兜底、自动勾选用户协议、账号密码自动填充、静默流程探针。
 */

/**
 * 页面脚本执行前注入：把 fetch / XHR 里的 x-rpc-combo_token 记到 window.__smCombo。
 * 子 frame（passport 登录页）抓到后通过 postMessage 汇总到顶层 frame。
 */
internal const val COMBO_HOOK_JS = """
(function () {
  try {
    window.__smCombo = window.__smCombo || '';
    // 注入脚本的操作日志：iframe 里发生的步骤会 postMessage 到顶层，顶层汇总后由 App 读取
    window.__smLog = window.__smLog || [];
    var lastCombo = window.__smCombo;
    function looks(v) {
      if (typeof v !== 'string' || v.length < 13) return false;
      var i = v.indexOf('ai=');
      if (i < 0) return false;
      if (i > 0 && v.charAt(i - 1) !== ';') return false;
      var d = v.charAt(i + 3);
      return d >= '0' && d <= '9' && v.indexOf(';', i) > 0;
    }
    function set(v) {
      if (!v) return;
      v = String(v);
      if (v.length < 8 || v.length > 8000) return;
      // 只记录本次页面会话里真实请求带出来的凭证；不落 sessionStorage，
      // 否则下次打开会读到上次的旧 combo_token，服务端判定失效就直接「假成功」。
      window.__smCombo = v;
      try {
        if (window.top && window.top !== window) window.top.postMessage({ __smCombo: v }, '*');
      } catch (e) {}
    }
    function pick(obj, depth) {
      if (!obj || depth > 6) return '';
      if (typeof obj === 'string') return looks(obj) ? obj : '';
      if (typeof obj !== 'object') return '';
      for (var k in obj) {
        try {
          var v = obj[k];
          if (/combo_token/i.test(k) && typeof v === 'string' && v.length > 8) return v;
          if (looks(v)) return v;
          var f = pick(v, depth + 1);
          if (f) return f;
        } catch (e) {}
      }
      return '';
    }
    try {
      window.addEventListener('message', function (e) {
        var d = e && e.data;
        if (!d) return;
        if (d.__smCombo) set(String(d.__smCombo));
        if (d.__smLoginError) window.__smLoginError = String(d.__smLoginError);
        if (d.__smCaptcha) window.__smCaptcha = true;
        if (d.__smLoginForm) window.__smLoginForm = true;
        if (d.__smLoginUi) window.__smLoginUi = true;
        if (d.__smSecurityAt) window.__smSecurityAt = Number(d.__smSecurityAt) || Date.now();
        // 登录动作发生后作废旧凭证，只认登录之后新下发的 combo_token
        if (d.__smClearCombo) window.__smCombo = '';
        if (d.__smLoginClicked) window.__smLoginClicked = true;
        if (d.__smLog) {
          try {
            window.__smLog = window.__smLog || [];
            window.__smLog.push(String(d.__smLog));
            if (window.__smLog.length > 300) window.__smLog.shift();
          } catch (err) {}
        }
      });
    } catch (e) {}
    // 登录成功后米哈游会跳到「正在检测您的账号安全」过渡页（cross-page.html），
    // WebView 里没人调原生回调会一直卡住。各 frame 定时上报时间戳，App 读到后自动返回。
    var securityTicks = 0;
    function securityTick() {
      try {
        securityTicks++;
        var href = String(location.href || '');
        var hit = /cross-page\.html/i.test(href);
        // innerText 会触发布局，别每 800ms 都读；URL 不匹配时每 4 秒兜底看一次文案
        if (!hit && securityTicks % 5 === 0) {
          var txt = ((document.body && (document.body.innerText || document.body.textContent)) || '');
          if (txt.indexOf('正在检测您的账号安全') >= 0) hit = true;
        }
        if (!hit) return;
        if (window.top && window.top !== window) {
          window.top.postMessage({ __smSecurityAt: Date.now() }, '*');
        } else {
          window.__smSecurityAt = Date.now();
        }
      } catch (e) {}
    }
    securityTick();
    try {
      var securityRounds = 0;
      var securityTimer = setInterval(function () {
        securityTick();
        if (++securityRounds > 900) clearInterval(securityTimer);
      }, 800);
    } catch (e) {}
    function hook(headers) {
      if (!headers) return;
      try {
        if (typeof headers.forEach === 'function' && !Array.isArray(headers)) {
          headers.forEach(function (v, k) {
            if (k && String(k).toLowerCase() === 'x-rpc-combo_token' && v) set(v);
          });
        } else {
          Object.keys(headers).forEach(function (k) {
            if (String(k).toLowerCase() === 'x-rpc-combo_token' && headers[k]) set(headers[k]);
          });
        }
      } catch (e) {}
    }
    var of = window.fetch;
    if (of) {
      window.fetch = function (input, init) {
        try {
          if (init && init.headers) hook(init.headers);
          if (input && input.headers) hook(input.headers);
        } catch (e) {}
        var p = of.apply(this, arguments);
        try {
          if (p && typeof p.then === 'function') {
            p.then(function (res) {
              try {
                if (!res || typeof res.clone !== 'function') return;
                res.clone().json().then(function (d) {
                  var v = pick(d, 0);
                  if (v) set(v);
                }).catch(function () {});
              } catch (e) {}
            }).catch(function () {});
          }
        } catch (e) {}
        return p;
      };
    }
    var ox = XMLHttpRequest.prototype.setRequestHeader;
    XMLHttpRequest.prototype.setRequestHeader = function (name, value) {
      try {
        if (name && String(name).toLowerCase() === 'x-rpc-combo_token' && value) set(value);
      } catch (e) {}
      return ox.apply(this, arguments);
    };
    var oo = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function () {
      var x = this;
      try {
        x.addEventListener('load', function () {
          try {
            var t = x.responseText;
            if (!t || t.length > 2000000 || t.indexOf('combo') < 0) return;
            var v = pick(JSON.parse(t), 0);
            if (v) set(v);
          } catch (e) {}
        });
      } catch (e) {}
      return oo.apply(this, arguments);
    };
  } catch (e) {}
})();
"""

/** 抓取时兜底：网络钩子没拿到时，翻 localStorage / sessionStorage / document.cookie。 */
internal const val COMBO_READ_JS = """
(function () {
  try { if (window.__smCombo) return String(window.__smCombo); } catch (e) {}
  function looks(v) {
    if (typeof v !== 'string' || v.length < 13) return false;
    var i = v.indexOf('ai=');
    if (i < 0) return false;
    if (i > 0 && v.charAt(i - 1) !== ';') return false;
    var d = v.charAt(i + 3);
    return d >= '0' && d <= '9' && v.indexOf(';', i) > 0;
  }
  function pick(obj, depth) {
    if (!obj || depth > 6) return '';
    if (typeof obj === 'string') return looks(obj) ? obj : '';
    if (typeof obj !== 'object') return '';
    for (var k in obj) {
      try {
        var v = obj[k];
        if (/combo_token/i.test(k) && typeof v === 'string' && v.length > 8) return v;
        if (looks(v)) return v;
        var f = pick(v, depth + 1);
        if (f) return f;
      } catch (e) {}
    }
    return '';
  }
  function scan(store) {
    try {
      for (var i = 0; i < store.length; i++) {
        var k = store.key(i) || '';
        var v = store.getItem(k);
        if (!v) continue;
        v = String(v);
        try {
          var got = pick(JSON.parse(v), 0);
          if (got) return got;
        } catch (e) {}
        if (/combo/i.test(k)) {
          if (looks(v)) return v;
          if (v.length > 8 && v.length < 8000) return v;
        }
        if (looks(v)) return v;
      }
    } catch (e) {}
    return '';
  }
  var v = scan(window.localStorage) || scan(window.sessionStorage);
  if (v) return v;
  var parts = (document.cookie || '').split(';');
  for (var j = 0; j < parts.length; j++) {
    var p = parts[j].trim();
    var idx = p.indexOf('=');
    if (idx > 0 && /combo/i.test(p.substring(0, idx))) {
      try { return decodeURIComponent(p.substring(idx + 1)); } catch (e) { return p.substring(idx + 1); }
    }
  }
  return '';
})();
"""

/**
 * 只负责勾选「同意用户协议」的脚本（手动登录页用）。
 * 米哈游登录页是 SPA，勾选框可能晚几秒才出现，所以用轮询 + MutationObserver 常驻。
 */
internal val AGREE_ONLY_JS: String = """
(function () {
  try {
    if (window.__smAgreeOnly) return;
    window.__smAgreeOnly = true;
    var clickedOnce = [];

    function text(el) {
      return ((el && (el.innerText || el.textContent)) || '').replace(/\s+/g, '');
    }
    function seen(el) {
      for (var i = 0; i < clickedOnce.length; i++) if (clickedOnce[i] === el) return true;
      return false;
    }
    function visible(el) {
      if (!el) return false;
      try {
        var r = el.getBoundingClientRect();
        if (r.width < 2 || r.height < 2) return false;
        var s = window.getComputedStyle(el);
        if (!s || s.display === 'none' || s.visibility === 'hidden' || parseFloat(s.opacity || '1') < 0.05) return false;
      } catch (e) {}
      return true;
    }
    function clickEl(el) {
      if (!el) return false;
      try { el.scrollIntoView({ block: 'center' }); el.click(); return true; } catch (e) { return false; }
    }
    function nearText(box) {
      try {
        var p = box.closest('label') || box.parentElement;
        return text(p).slice(0, 80);
      } catch (e) { return ''; }
    }
    function agreeBox() {
      var boxes = document.querySelectorAll('input[type="checkbox"]');
      var only = boxes.length === 1 ? boxes[0] : null;
      for (var i = 0; i < boxes.length; i++) {
        var b = boxes[i];
        var t = nearText(b);
        if (!/同意|协议|条款|隐私/.test(t) && b !== only) continue;
        if (b.checked) return true;
        try { b.click(); } catch (e) {}
        if (b.checked) return true;
        var label = b.closest('label');
        if (label) clickEl(label);
        if (b.checked) return true;
      }
      var nodes = document.querySelectorAll('span,div,p,label,i');
      for (var j = 0; j < nodes.length; j++) {
        var el = nodes[j];
        if (el.children && el.children.length > 2) continue;
        var s = text(el);
        if (!s || s.length > 40) continue;
        if (!/同意/.test(s) || !/协议|条款|隐私/.test(s)) continue;
        if (el.tagName === 'A') continue;
        if (!visible(el)) continue;
        if (seen(el)) continue;
        clickedOnce.push(el);
        clickEl(el);
        return true;
      }
      return false;
    }
    function agreeDialog() {
      var nodes = document.querySelectorAll('button,[role="button"],a,span,div');
      for (var i = 0; i < nodes.length; i++) {
        var el = nodes[i];
        var s = text(el);
        if (s !== '同意' && s !== '我同意' && s !== '同意并继续' && s !== '接受') continue;
        if (!visible(el)) continue;
        var box = el.closest('[class*="dialog"],[class*="modal"],[role="dialog"]') || el.parentElement;
        if (!/协议|条款|隐私/.test(text(box).slice(0, 300))) continue;
        clickEl(el);
        return true;
      }
      return false;
    }
    function pass() {
      try { agreeBox() || agreeDialog(); } catch (e) {}
    }
    pass();
    var timer = setInterval(pass, 1200);
    setTimeout(function () { clearInterval(timer); }, 180000);
    try {
      var scheduled = false;
      var obs = new MutationObserver(function () {
        if (scheduled) return;
        scheduled = true;
        setTimeout(function () { scheduled = false; pass(); }, 500);
      });
      obs.observe(document.documentElement || document, { childList: true, subtree: true });
    } catch (e) {}
  } catch (e) {}
})();
"""

private const val AUTO_FILL_TEMPLATE = """
(function () {
  try {
    if (window.__smAutoFill) return;
    window.__smAutoFill = true;
    // 账号密码只允许填进米哈游自己的域名，第三方 iframe 一律不动
    var host = '';
    try { host = String(location.hostname || '').toLowerCase(); } catch (e) {}
    var miHost = host === 'mihoyo.com' || host.endsWith('.mihoyo.com') ||
      host === 'hoyoverse.com' || host.endsWith('.hoyoverse.com');
    if (!miHost) return;
    L('自动登录脚本已加载（' + (host || 'unknown') + '）');
    L('页面加载完成后等待 3 秒再自动登录');
    var ACC = __SM_ACCOUNT__;
    var PWD = __SM_PASSWORD__;
    var rounds = 0;
    var clickedLogin = 0;
    var pwdTries = 0;
    var lastPwdClick = 0;
    var lastClick = 0;
    var fillRounds = 0;
    var halted = false;
    var clickedOnce = [];
    var formLogged = false;
    var fillLogged = false;
    var errorLogged = false;
    var capWas = false;
    var limitLogged = false;
    var pwdMissLogged = false;
    var lastRouteTry = 0;
    var lastIframeTry = 0;
    var textsDumped = false;
    var uiPosted = false;

    // 登录页 iframe 一打开就告诉顶层「登录界面已出现」，避免 App 把旧凭证当成已登录
    try {
      if (/login/i.test(String(location.href || ''))) {
        uiPosted = true;
        if (window.top && window.top !== window) {
          window.top.postMessage({ __smLoginUi: true }, '*');
        } else {
          window.__smLoginUi = true;
        }
      }
    } catch (e) {}

    // 页面 load 完成后再等 3 秒，等登录弹窗渲染稳定再切「密码登录」
    var startAt = Date.now() + 3000;
    try {
      if (document.readyState !== 'complete') {
        window.addEventListener('load', function () {
          startAt = Date.now() + 3000;
        });
      }
    } catch (e) {}

    // 操作日志：同域直接挂到 __smLog，跨 frame postMessage 汇总到顶层
    function L(msg) {
      try {
        if (window.top && window.top !== window) {
          window.top.postMessage({ __smLog: String(msg) }, '*');
        } else {
          window.__smLog = window.__smLog || [];
          window.__smLog.push(String(msg));
          if (window.__smLog.length > 300) window.__smLog.shift();
        }
      } catch (e) {}
    }

    function text(el) {
      return ((el && (el.innerText || el.textContent)) || '').replace(/\s+/g, '');
    }
    function seen(el) {
      for (var i = 0; i < clickedOnce.length; i++) if (clickedOnce[i] === el) return true;
      return false;
    }
    function visible(el) {
      if (!el) return false;
      try {
        var r = el.getBoundingClientRect();
        if (r.width < 2 || r.height < 2) return false;
        var s = window.getComputedStyle(el);
        if (!s || s.display === 'none' || s.visibility === 'hidden' || parseFloat(s.opacity || '1') < 0.05) return false;
      } catch (e) {}
      return true;
    }
    function clickEl(el) {
      if (!el) return false;
      try { el.scrollIntoView({ block: 'center' }); el.click(); return true; } catch (e) { return false; }
    }
    function setVal(el, val) {
      try {
        var desc = Object.getOwnPropertyDescriptor(el.constructor.prototype, 'value');
        if (desc && desc.set) desc.set.call(el, val); else el.value = val;
      } catch (e) { el.value = val; }
      try {
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
      } catch (e) {}
    }
    function nearText(box) {
      try {
        var p = box.closest('label') || box.parentElement;
        return text(p).slice(0, 80);
      } catch (e) { return ''; }
    }
    function agreeBox() {
      var boxes = document.querySelectorAll('input[type="checkbox"]');
      var only = boxes.length === 1 ? boxes[0] : null;
      for (var i = 0; i < boxes.length; i++) {
        var b = boxes[i];
        var t = nearText(b);
        if (!/同意|协议|条款|隐私/.test(t) && b !== only) continue;
        if (b.checked) return true;
        try { b.click(); } catch (e) {}
        if (b.checked) { L('已勾选「同意用户协议」'); return true; }
        var label = b.closest('label');
        if (label) clickEl(label);
        if (b.checked) { L('已勾选「同意用户协议」'); return true; }
      }
      var nodes = document.querySelectorAll('span,div,p,label,i');
      for (var j = 0; j < nodes.length; j++) {
        var el = nodes[j];
        if (el.children && el.children.length > 2) continue;
        var s = text(el);
        if (!s || s.length > 40) continue;
        if (!/同意/.test(s) || !/协议|条款|隐私/.test(s)) continue;
        if (el.tagName === 'A') continue;
        if (!visible(el)) continue;
        if (seen(el)) continue;
        clickedOnce.push(el);
        clickEl(el);
        L('已勾选「同意用户协议」');
        return true;
      }
      return false;
    }
    function agreeDialog() {
      var nodes = document.querySelectorAll('button,[role="button"],a,span,div');
      for (var i = 0; i < nodes.length; i++) {
        var el = nodes[i];
        var s = text(el);
        if (s !== '同意' && s !== '我同意' && s !== '同意并继续' && s !== '接受') continue;
        if (!visible(el)) continue;
        var box = el.closest('[class*="dialog"],[class*="modal"],[role="dialog"]') || el.parentElement;
        if (!/协议|条款|隐私/.test(text(box).slice(0, 300))) continue;
        clickEl(el);
        L('已确认协议弹窗');
        return true;
      }
      return false;
    }
    function hasPasswordInput() {
      var inputs = document.querySelectorAll('input[type="password"]');
      for (var i = 0; i < inputs.length; i++) {
        if (visible(inputs[i])) return true;
      }
      return false;
    }
    // 登录页默认停在「短信验证码登录」，这里的文案在不同版本里不太一样，用包含匹配
    function passwordTab() {
      var nodes = document.querySelectorAll('div,span,a,button,p,li,em,strong,label');
      var best = null;
      var bestLen = 999;
      for (var i = 0; i < nodes.length; i++) {
        var el = nodes[i];
        var s = text(el);
        if (!s || s.length > 14) continue;
        if (!/密码(登录|登陆)/.test(s) &&
            s.indexOf('账号密码') < 0 && s.indexOf('帐号密码') < 0) continue;
        if (!visible(el)) continue;
        // 文案相同的情况下越靠后（越里层）越优先，避免点到大容器没点到 tab
        if (s.length <= bestLen) {
          bestLen = s.length;
          best = el;
        }
      }
      return best;
    }
    // 米哈游通行证是 hash 路由 SPA：#/login/captcha 是短信验证码登录，
    // #/login/password 是密码登录。直接改路由比在页面上找「密码登录」按钮可靠。
    function passwordRoute() {
      var now = Date.now();
      if (now - lastRouteTry < 2000) return false;
      lastRouteTry = now;
      try {
        var h = String(location.hash || '');
        if (/login\/(captcha|code|sms)/i.test(h)) {
          location.hash = h.replace(/login\/(captcha|code|sms)/i, 'login/password');
          L('直接切换通行证路由到密码登录');
          return true;
        }
      } catch (e) {}
      return false;
    }
    // 登录弹窗可能是 user.mihoyo.com 的 iframe，父页面改 src 的 hash 也能切过去
    function passwordIframe() {
      var now = Date.now();
      if (now - lastIframeTry < 3000) return false;
      lastIframeTry = now;
      try {
        var frames = document.querySelectorAll('iframe');
        for (var i = 0; i < frames.length; i++) {
          var src = frames[i].src || '';
          if (!/user\.mihoyo\.com|mihoyo.*passport/i.test(src)) continue;
          if (!/#\/login\/(captcha|code|sms)/i.test(src)) continue;
          frames[i].src = src.replace(/#\/login\/(captcha|code|sms)/i, '#/login/password');
          L('切换登录 iframe 到密码登录');
          return true;
        }
      } catch (e) {}
      return false;
    }
    // 找不到入口时把页面上可见的短文案打进日志，方便定位真实按钮叫什么
    function dumpTexts() {
      if (textsDumped) return;
      textsDumped = true;
      try {
        var nodes = document.querySelectorAll('button,span,a,div,label');
        var out = [];
        var seenTxt = {};
        for (var i = 0; i < nodes.length && out.length < 24; i++) {
          var s = text(nodes[i]);
          if (!s || s.length > 10) continue;
          if (!visible(nodes[i])) continue;
          if (seenTxt[s]) continue;
          seenTxt[s] = 1;
          out.push(s);
        }
        if (out.length) L('页面可见文案：' + out.join(' / '));
      } catch (e) {}
    }
    function passwordMode() {
      if (hasPasswordInput()) return;
      if (passwordRoute() || passwordIframe()) return;
      if (Date.now() > startAt + 2500) dumpTexts();
      if (pwdTries >= 20) return;
      var now = Date.now();
      if (now - lastPwdClick < 1800) return;
      var el = passwordTab();
      if (!el) {
        if (!pwdMissLogged) {
          pwdMissLogged = true;
          L('页面未找到「密码登录」入口，保持当前登录方式');
        }
        return;
      }
      lastPwdClick = now;
      pwdTries++;
      clickEl(el);
      L('点击「密码登录」切换登录方式');
    }
    function fill() {
      var pw = null;
      var inputs = document.querySelectorAll('input');
      for (var i = 0; i < inputs.length; i++) {
        var it = inputs[i];
        if ((it.type || '').toLowerCase() === 'password' && visible(it)) { pw = it; break; }
      }
      if (!pw) return false;
      if (!formLogged) { formLogged = true; L('检测到账号密码输入框'); }
      try { window.top.postMessage({ __smLoginForm: true }, '*'); } catch (e) {}
      var scope = pw.closest('form');
      var list = scope ? scope.querySelectorAll('input') : inputs;
      var user = null;
      for (var j = 0; j < list.length; j++) {
        var it2 = list[j];
        if (it2 === pw) continue;
        var ty = (it2.type || 'text').toLowerCase();
        if (ty !== 'text' && ty !== 'tel' && ty !== 'email' && ty !== 'number') continue;
        if (!visible(it2)) continue;
        user = it2;
        break;
      }
      if (!user) return false;
      if (user.value !== ACC) setVal(user, ACC);
      if (pw.value !== PWD) setVal(pw, PWD);
      if (!fillLogged) { fillLogged = true; L('已填写账号和密码'); }
      return user.value === ACC && pw.value === PWD;
    }
    function loginButton() {
      var exact = ['登录', '登 录', '立即登录', '账号登录', '安全登录'];
      var nodes = document.querySelectorAll('button,[role="button"],a,div,span');
      for (var i = 0; i < nodes.length; i++) {
        var el = nodes[i];
        var s = text(el);
        if (!s) continue;
        for (var k = 0; k < exact.length; k++) {
          if (s === exact[k] && visible(el)) return el;
        }
      }
      return null;
    }
    function clickLogin() {
      if (clickedLogin >= 5) return;
      var btn = loginButton();
      if (!btn) return;
      var now = Date.now();
      if (now - lastClick < 2500) return;
      lastClick = now;
      clickedLogin++;
      clickEl(btn);
      if (clickedLogin === 1) {
        L('已点击「登录」按钮');
        // 点击登录后作废之前页面缓存里带出来的旧 combo_token，只认登录后新下发的
        try { window.__smCombo = ''; } catch (e) {}
        try {
          if (window.top && window.top !== window) {
            window.top.postMessage({ __smClearCombo: true }, '*');
            window.top.postMessage({ __smLoginClicked: true }, '*');
          } else {
            window.__smLoginClicked = true;
          }
        } catch (e) {}
      }
    }
    function report() {
      var bodyText = '';
      try {
        bodyText = ((document.body && (document.body.innerText || document.body.textContent)) || '').slice(0, 4000);
      } catch (e) {}
      if (/账号或密码错误|密码错误|密码不正确|账号不存在|账号未注册/.test(bodyText)) {
        if (!errorLogged) { errorLogged = true; L('检测到「账号或密码错误」提示'); }
        try { window.top.postMessage({ __smLoginError: '账号或密码错误，请更新本机保存的账号密码' }, '*'); } catch (e) {}
      }
      var cap = false;
      try {
        // 只认「真的显示出来」的验证码，避免页面上预埋的隐藏 geetest 容器导致误判
        // 只认真正的 geetest 组件；登录页自身的 class/id 里也可能带 captcha（短信登录路由），
        // 之前用 [class*="captcha"] 会误判成验证码，导致自动登录直接停住。
        var cands = document.querySelectorAll('.geetest_holder,.geetest_panel,.geetest_widget,[class*="geetest"],iframe[src*="geetest"]');
        for (var ci = 0; ci < cands.length; ci++) {
          var ce = cands[ci];
          var cs = window.getComputedStyle(ce);
          if (!cs || cs.display === 'none' || cs.visibility === 'hidden') continue;
          var cr = ce.getBoundingClientRect();
          if (cr.width > 40 && cr.height > 20) { cap = true; break; }
        }
      } catch (e) {}
      if (/请完成验证|拖动滑块|滑动验证|请选择所有符合|请完成安全验证/.test(bodyText)) cap = true;
      if (!uiPosted) {
        try {
          var telEl = document.querySelector('input[type="tel"],input[placeholder*="手机"],input[placeholder*="验证码"]');
          if ((telEl && visible(telEl)) || /短信登录|验证码登录|短信验证码/.test(bodyText)) {
            uiPosted = true;
            if (window.top && window.top !== window) {
              window.top.postMessage({ __smLoginUi: true }, '*');
            } else {
              window.__smLoginUi = true;
            }
          }
        } catch (e) {}
      }
      if (cap) {
        // 出现人机验证时停下来，避免和用户手动操作抢输入框
        halted = true;
        if (!capWas) { capWas = true; L('检测到安全验证，已暂停自动操作'); }
        try { window.top.postMessage({ __smCaptcha: true }, '*'); } catch (e) {}
      } else {
        if (capWas) { capWas = false; L('安全验证已通过，继续自动登录'); }
        if (halted) halted = false;
      }
    }
    function pass() {
      try {
        if (Date.now() < startAt) return;
        report();
        agreeBox() || agreeDialog();
        // 先切登录方式再判断验证码：短信登录路由里的 captcha 类名容易被误判，
        // 不能因为误判把切密码登录也一起挡住
        passwordMode();
        if (halted) return;
        if (fillRounds >= 30) {
          if (!limitLogged) { limitLogged = true; L('自动尝试次数用尽，转为手动接管'); }
          return;
        }
        if (fill()) {
          fillRounds++;
          clickLogin();
        }
      } catch (e) {}
    }
    function loop() {
      if (rounds++ > 300) return;
      pass();
      setTimeout(loop, 1200);
    }
    loop();
    try {
      var scheduled = false;
      var obs = new MutationObserver(function () {
        if (scheduled) return;
        scheduled = true;
        setTimeout(function () { scheduled = false; pass(); }, 600);
      });
      obs.observe(document.documentElement || document, { childList: true, subtree: true });
    } catch (e) {}
  } catch (e) {}
})();
"""

/** 静默流程状态探针：返回 JSON 字符串（combo / error / captcha / login）。 */
internal const val SILENT_PROBE_JS = """
(function () {
  function s(v) { return v ? String(v) : ''; }
  var out = { combo: '', error: '', captcha: false, login: false, autoLogin: false, security: false, url: '', logs: [] };
  try { out.combo = s(window.__smCombo); } catch (e) {}
  try { out.error = s(window.__smLoginError); } catch (e) {}
  try { out.captcha = !!window.__smCaptcha; } catch (e) {}
  try { out.login = !!window.__smLoginForm || !!window.__smLoginUi; } catch (e) {}
  try { out.autoLogin = !!window.__smLoginClicked; } catch (e) {}
  try { out.security = (Date.now() - (window.__smSecurityAt || 0)) < 2000; } catch (e) {}
  try { out.url = s(location.href); } catch (e) {}
  try { out.logs = (window.__smLog || []); } catch (e) {}
  if (!out.login) {
    try {
      // 短信验证码登录面板没有密码框，用手机号输入框判断登录弹窗是否已经出现
      var tel = document.querySelector('input[type="tel"],input[placeholder*="手机"],input[placeholder*="验证码"]');
      if (tel) {
        var tr = tel.getBoundingClientRect();
        if (tr.width > 2 && tr.height > 2) out.login = true;
      }
    } catch (e) {}
  }
  if (!out.login) {
    try {
      var bodyTxt = ((document.body && (document.body.innerText || document.body.textContent)) || '').slice(0, 2000);
      if (/短信登录|验证码登录|短信验证码/.test(bodyTxt)) out.login = true;
    } catch (e) {}
  }
  if (!out.login) {
    try {
      var inputs = document.querySelectorAll('input[type="password"]');
      for (var i = 0; i < inputs.length; i++) {
        var r = inputs[i].getBoundingClientRect();
        if (r.width > 2 && r.height > 2) { out.login = true; break; }
      }
    } catch (e) {}
  }
  if (!out.captcha) {
    try {
      var cands = document.querySelectorAll('.geetest_holder,.geetest_panel,.geetest_widget,[class*="geetest"],iframe[src*="geetest"]');
      for (var ci = 0; ci < cands.length; ci++) {
        var ce = cands[ci];
        var cs = window.getComputedStyle(ce);
        if (!cs || cs.display === 'none' || cs.visibility === 'hidden') continue;
        var cr = ce.getBoundingClientRect();
        if (cr.width > 40 && cr.height > 20) { out.captcha = true; break; }
      }
    } catch (e) {}
  }
  try { return JSON.stringify(out); } catch (e) { return '{"combo":"","error":"","captcha":false,"login":false,"url":"","logs":[]}'; }
})();
"""

/** 手动抓取页探针：是否停在「正在检测您的账号安全」过渡页。 */
internal const val SECURITY_CHECK_JS = """
(function () {
  try {
    return (Date.now() - (window.__smSecurityAt || 0) < 2000) ? '1' : '';
  } catch (e) { return ''; }
})();
"""

/** 账号密码注入前统一做 JSON 字符串转义，避免引号 / 反斜杠破坏脚本。 */
internal fun buildAutoFillJs(account: String, password: String): String =
    AUTO_FILL_TEMPLATE
        .replace("__SM_ACCOUNT__", JSONObject.quote(account))
        .replace("__SM_PASSWORD__", JSONObject.quote(password))

/** evaluateJavascript 返回值是 JSON 字面量（字符串会带引号），这里解回普通字符串。 */
internal fun jsString(raw: String?): String {
    val text = raw?.trim().orEmpty()
    if (text.isEmpty() || text == "null") return ""
    return runCatching { JSONTokener(text).nextValue() as? String }.getOrNull().orEmpty()
}

/** evaluateJavascript 一定要在主线程调用；这里封成挂起函数方便轮询。 */
internal suspend fun WebView.evalJs(script: String): String? =
    suspendCancellableCoroutine { continuation ->
        runCatching {
            evaluateJavascript(script) { result ->
                if (continuation.isActive) continuation.resume(result)
            }
        }.onFailure {
            if (continuation.isActive) continuation.resume(null)
        }
    }
