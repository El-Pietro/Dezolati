// Drobnosti, které C# v prohlížeči sám neumí: úložiště, signál, návrat do appky, chvění.
window.dz = (function () {
    var PREFIX = 'dz:';
    var appRef = null;
    var updateWaiting = false;

    function safe(fn, fallback) {
        try { return fn(); } catch (e) { return fallback; }
    }

    return {
        // ---- úložiště (Preferences v appce) ----
        storageAll: function () {
            return safe(function () {
                var out = {};
                for (var i = 0; i < localStorage.length; i++) {
                    var k = localStorage.key(i);
                    if (k && k.indexOf(PREFIX) === 0) out[k] = localStorage.getItem(k);
                }
                return out;
            }, {});
        },
        storageSet: function (k, v) {
            safe(function () {
                if (v === null || v === undefined) localStorage.removeItem(k);
                else localStorage.setItem(k, v);
            });
        },
        // ať Safari data webu přidaného na plochu nemaže
        persist: function () {
            safe(function () { if (navigator.storage && navigator.storage.persist) navigator.storage.persist(); });
        },

        // ---- signál a návrat do appky ----
        isOnline: function () { return navigator.onLine !== false; },
        hook: function (ref) {
            appRef = ref;
            window.addEventListener('online', function () { ref.invokeMethodAsync('OnOnline', true); });
            window.addEventListener('offline', function () { ref.invokeMethodAsync('OnOnline', false); });
            document.addEventListener('visibilitychange', function () {
                if (document.visibilityState === 'visible') ref.invokeMethodAsync('OnResume');
            });
            if (updateWaiting) ref.invokeMethodAsync('OnUpdateReady');
        },
        updateReady: function () {
            updateWaiting = true;
            if (appRef) appRef.invokeMethodAsync('OnUpdateReady');
        },
        // nová verze čeká → přepnout na ni a načíst znovu
        reload: function () {
            var reg = window.dzReg;
            if (reg && reg.waiting) {
                navigator.serviceWorker.addEventListener('controllerchange', function () { location.reload(); });
                reg.waiting.postMessage('skipWaiting');
            } else {
                location.reload();
            }
        },

        // ---- drobnosti ----
        vibrate: function (ms) { safe(function () { if (navigator.vibrate) navigator.vibrate(ms); }); },
        isStandalone: function () {
            return safe(function () {
                return window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true;
            }, false);
        },
        isIos: function () {
            return /iPad|iPhone|iPod/.test(navigator.userAgent) ||
                (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
        },
        scrollTop: function () { safe(function () { window.scrollTo(0, 0); }); }
    };
})();
