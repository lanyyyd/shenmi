package main

import (
	"context"
	"time"

	"github.com/chromedp/cdproto/page"
	"github.com/chromedp/chromedp"
)

// interceptJS is _INTERCEPT_JS, copied verbatim from browser_driver.py.
const interceptJS = `
window.__captured_combo_token = '';
(function(){
  function looks(v){
    if (typeof v !== 'string' || v.length < 13) return false;
    var i = v.indexOf('ai=');
    if (i < 0) return false;
    if (i > 0 && v.charAt(i - 1) !== ';') return false;
    var d = v.charAt(i + 3);
    return d >= '0' && d <= '9' && v.indexOf(';', i) > 0;
  }
  function set(v){
    if (!v) return;
    v = String(v);
    if (v.length < 8 || v.length > 8000) return;
    window.__captured_combo_token = v;
    try { if (window.top && window.top !== window) window.top.postMessage({__smCombo: v}, '*'); } catch(e){}
  }
  function pick(o, depth){
    if (!o || depth > 6) return '';
    if (typeof o === 'string') return looks(o) ? o : '';
    if (typeof o !== 'object') return '';
    for (var k in o) {
      try {
        var v = o[k];
        if (/combo_token/i.test(k) && typeof v === 'string' && v.length > 8) return v;
        if (looks(v)) return v;
        var f = pick(v, depth + 1);
        if (f) return f;
      } catch(e){}
    }
    return '';
  }
  try {
    window.addEventListener('message', function(e){
      var d = e && e.data;
      if (d && d.__smCombo) window.__captured_combo_token = String(d.__smCombo);
    });
  } catch(e){}
  const origFetch = window.fetch;
  window.fetch = function(input, init){
    try {
      if (init && init.headers) {
        const h = init.headers;
        if (h instanceof Headers) {
          h.forEach(function(v, k){
            if (k.toLowerCase() === 'x-rpc-combo_token' && v) set(v);
          });
        } else {
          Object.keys(h).forEach(function(k){
            if (k.toLowerCase() === 'x-rpc-combo_token' && h[k]) set(h[k]);
          });
        }
      }
    } catch(e){}
    const p = origFetch.apply(this, arguments);
    try {
      if (p && typeof p.then === 'function') {
        p.then(function(res){
          try {
            if (!res || typeof res.clone !== 'function') return;
            res.clone().json().then(function(d){
              var v = pick(d, 0);
              if (v) set(v);
            }).catch(function(){});
          } catch(e){}
        }).catch(function(){});
      }
    } catch(e){}
    return p;
  };
  const origSetHeader = XMLHttpRequest.prototype.setRequestHeader;
  XMLHttpRequest.prototype.setRequestHeader = function(name, value){
    try {
      if (name.toLowerCase() === 'x-rpc-combo_token' && value) set(value);
    } catch(e){}
    return origSetHeader.apply(this, arguments);
  };
  const origOpen = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function(){
    const x = this;
    try {
      x.addEventListener('load', function(){
        try {
          const t = x.responseText;
          if (!t || t.length > 2000000 || t.indexOf('combo') < 0) return;
          var v = pick(JSON.parse(t), 0);
          if (v) set(v);
        } catch(e){}
      });
    } catch(e){}
    return origOpen.apply(this, arguments);
  };
})();
`

// readComboJS is _READ_COMBO_JS, copied verbatim from browser_driver.py.
const readComboJS = `
(function () {
  try { if (window.__captured_combo_token) return String(window.__captured_combo_token); } catch (e) {}
  function looks(v) {
    if (typeof v !== 'string' || v.length < 13) return false;
    var i = v.indexOf('ai=');
    if (i < 0) return false;
    if (i > 0 && v.charAt(i - 1) !== ';') return false;
    var d = v.charAt(i + 3);
    return d >= '0' && d <= '9' && v.indexOf(';', i) > 0;
  }
  function pick(o, depth) {
    if (!o || depth > 6) return '';
    if (typeof o === 'string') return looks(o) ? o : '';
    if (typeof o !== 'object') return '';
    for (var k in o) {
      try {
        var v = o[k];
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
`

// captureComboToken is capture_combo_token. The network listener installed in
// setupTarget replaces Selenium's performance log fallback.
func captureComboToken(s *session, timeout int) string {
	_ = s.run(10*time.Second, chromedp.ActionFunc(func(ctx context.Context) error {
		_, err := page.AddScriptToEvaluateOnNewDocument(interceptJS).Do(ctx)
		return err
	}))

	_ = s.reload()

	deadline := time.Now().Add(time.Duration(timeout) * time.Second)
	token := ""
	for time.Now().Before(deadline) {
		var val string
		if err := s.eval("window.__captured_combo_token || ''", &val); err == nil && val != "" {
			token = val
			break
		}
		time.Sleep(1 * time.Second)
	}

	if token == "" {
		var val string
		if err := s.eval(readComboJS, &val); err == nil {
			token = val
		}
	}

	if token == "" {
		token = s.takeCombo()
	}
	return token
}
