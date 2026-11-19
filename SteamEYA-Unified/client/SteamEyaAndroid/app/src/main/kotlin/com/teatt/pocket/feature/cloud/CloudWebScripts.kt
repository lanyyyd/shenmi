package com.teatt.pocket.feature.cloud

import org.json.JSONObject
import org.json.JSONTokener

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
      window.__smCombo = v;
      // 同一 origin 内翻页后还能读到，避免登录成功后跳转丢掉凭证
      try { window.sessionStorage.setItem('__smCombo', v); } catch (e) {}
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
      var cached = window.sessionStorage.getItem('__smCombo');
      if (cached && !window.__smCombo) window.__smCombo = cached;
    } catch (e) {}
    try {
      window.addEventListener('message', function (e) {
        var d = e && e.data;
        if (!d) return;
        if (d.__smCombo) set(String(d.__smCombo));
        if (d.__smLoginError) window.__smLoginError = String(d.__smLoginError);
        if (d.__smCaptcha) window.__smCaptcha = true;
        if (d.__smLoginForm) window.__smLoginForm = true;
      });
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
    var ACC = __SM_ACCOUNT__;
    var PWD = __SM_PASSWORD__;
    var rounds = 0;
    var clickedLogin = 0;
    var pwdSwitched = false;
    var lastClick = 0;
    var fillRounds = 0;
    var halted = false;
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
    function passwordMode() {
      if (pwdSwitched) return;
      var nodes = document.querySelectorAll('div,span,a,button');
      for (var i = 0; i < nodes.length; i++) {
        var s = text(nodes[i]);
        if (s === '密码登录' || s === '密码登陆' || s === '账号密码登录') {
          if (visible(nodes[i])) {
            pwdSwitched = true;
            clickEl(nodes[i]);
            return;
          }
        }
      }
    }
    function fill() {
      var pw = null;
      var inputs = document.querySelectorAll('input');
      for (var i = 0; i < inputs.length; i++) {
        var it = inputs[i];
        if ((it.type || '').toLowerCase() === 'password' && visible(it)) { pw = it; break; }
      }
      if (!pw) return false;
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
    }
    function report() {
      var bodyText = '';
      try {
        bodyText = ((document.body && (document.body.innerText || document.body.textContent)) || '').slice(0, 4000);
      } catch (e) {}
      if (/账号或密码错误|密码错误|密码不正确|账号不存在|账号未注册/.test(bodyText)) {
        try { window.top.postMessage({ __smLoginError: '账号或密码错误，请更新本机保存的账号密码' }, '*'); } catch (e) {}
      }
      var cap = false;
      try {
        // 只认「真的显示出来」的验证码，避免页面上预埋的隐藏 geetest 容器导致误判
        var cands = document.querySelectorAll('.geetest_holder,.geetest_panel,.geetest_widget,iframe[src*="captcha"],[class*="captcha"],[id*="captcha"]');
        for (var ci = 0; ci < cands.length; ci++) {
          var ce = cands[ci];
          var cs = window.getComputedStyle(ce);
          if (!cs || cs.display === 'none' || cs.visibility === 'hidden') continue;
          var cr = ce.getBoundingClientRect();
          if (cr.width > 40 && cr.height > 20) { cap = true; break; }
        }
      } catch (e) {}
      if (/请完成验证|拖动滑块|安全验证|请选择所有符合/.test(bodyText)) cap = true;
      if (cap) {
        // 出现人机验证时停下来，避免和用户手动操作抢输入框
        halted = true;
        try { window.top.postMessage({ __smCaptcha: true }, '*'); } catch (e) {}
      } else if (halted) {
        halted = false;
      }
    }
    function pass() {
      try {
        report();
        agreeBox() || agreeDialog();
        // 验证中 / 自动填充轮次用尽后不再抢输入框，交给用户手动完成
        if (halted || fillRounds >= 30) return;
        passwordMode();
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
  var out = { combo: '', error: '', captcha: false, login: false };
  try { out.combo = s(window.__smCombo); } catch (e) {}
  try { out.error = s(window.__smLoginError); } catch (e) {}
  try { out.captcha = !!window.__smCaptcha; } catch (e) {}
  try { out.login = !!window.__smLoginForm; } catch (e) {}
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
      var cands = document.querySelectorAll('.geetest_holder,.geetest_panel,.geetest_widget,iframe[src*="captcha"],[class*="captcha"],[id*="captcha"]');
      for (var ci = 0; ci < cands.length; ci++) {
        var ce = cands[ci];
        var cs = window.getComputedStyle(ce);
        if (!cs || cs.display === 'none' || cs.visibility === 'hidden') continue;
        var cr = ce.getBoundingClientRect();
        if (cr.width > 40 && cr.height > 20) { out.captcha = true; break; }
      }
    } catch (e) {}
  }
  try { return JSON.stringify(out); } catch (e) { return '{"combo":"","error":"","captcha":false,"login":false}'; }
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
