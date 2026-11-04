/**
 * HIS2 ARM 前端授權狀態機 (BFF Cookie 架構版)
 * 💡 架構特徵：不再經手任何 JWT Token 字串，純粹負責到期倒數、背景 Refresh 與路由守衛。
 * 依賴：jQuery
 */
const ArmAuthManager = (function ($) {
    'use strict';

    // ==========================================
    // 1. 私有狀態 (Private State)
    // ==========================================
    const SESSION_KEY = 'ARM_SESSION_META';

    // 只快取 UI 呈現需要的 Metadata 與過期時間
    let sessionMeta = null;
    let heartbeatTimer = null;
    let isRefreshing = false;

    // ==========================================
    // 2. 內部輔助方法 (Private Methods)
    // ==========================================

    // 取得基礎 API 路徑 (支援 F5 或本機 YARP 的跨環境配置)
    const getBaseUrl = () => {
        return (window.__CONFIG__ && window.__CONFIG__.armBaseUrl)
            ? window.__CONFIG__.armBaseUrl
            : '/ArmV2'; // 預設降級路徑
    };

    const _loadFromStorage = () => {
        try {
            const data = sessionStorage.getItem(SESSION_KEY);
            if (data) {
                sessionMeta = JSON.parse(data);
            }
        } catch (e) {
            console.error('讀取授權中繼資料失敗', e);
            _clearSession();
        }
    };

    const _clearSession = () => {
        sessionMeta = null;
        sessionStorage.removeItem(SESSION_KEY);
        if (heartbeatTimer) clearInterval(heartbeatTimer);
    };

    // 背景無縫刷新機制 (Silent Refresh)
    const _performSilentRefresh = () => {
        if (isRefreshing) return;
        isRefreshing = true;

        // 💡 關鍵：因為是 HttpOnly 模式，這裡不帶任何 Payload 或 Header，
        // 瀏覽器會自動把名為 ARM_REFRESH_TOKEN 的 Cookie 送出。
        $.ajax({
            url: `${getBaseUrl()}/api/v1/Auth/refresh`,
            type: 'POST',
            xhrFields: { withCredentials: true }, // 確保跨域路徑時攜帶 Cookie
            success: function (res) {
                // 更新狀態機內的過期時間
                if (sessionMeta && res.accessExpiresAt) {
                    sessionMeta.accessExpiresAt = new Date(res.accessExpiresAt).getTime();
                    sessionStorage.setItem(SESSION_KEY, JSON.stringify(sessionMeta));
                    console.log('[ARM Auth] 憑證背景展期成功。');
                }
            },
            error: function () {
                console.warn('[ARM Auth] 憑證展期失敗，Session 已失效。');
                ArmAuthManager.logout(true);
            },
            complete: function () {
                isRefreshing = false;
            }
        });
    };

    // 心跳偵測器：每分鐘檢查一次是否快過期
    const _startHeartbeat = () => {
        if (heartbeatTimer) clearInterval(heartbeatTimer);

        heartbeatTimer = setInterval(() => {
            if (!sessionMeta || !sessionMeta.accessExpiresAt) return;

            const now = Date.now();
            const timeRemaining = sessionMeta.accessExpiresAt - now;
            const threshold = 5 * 60 * 1000; // 5分鐘 (毫秒)

            // 若剩餘壽命 < 5分鐘，觸發背景刷新
            if (timeRemaining > 0 && timeRemaining <= threshold) {
                _performSilentRefresh();
            }
            // 若已經過期 (可能電腦休眠剛醒來)
            else if (timeRemaining <= 0) {
                console.warn('[ARM Auth] 偵測到憑證已逾期，強制登出。');
                ArmAuthManager.logout(true);
            }
        }, 60 * 1000); // 每 60 秒檢查一次
    };

    // 全域 AJAX 攔截器 (只攔截 401，不負責塞 Token)
    const _setupGlobalAjaxInterceptor = () => {
        // 為所有 jQuery Ajax 預設開啟攜帶 Cookie 的能力
        $.ajaxSetup({
            xhrFields: { withCredentials: true }
        });

        // 全域捕捉 HTTP 401 (未授權) 或 403 (無權限) 錯誤
        $(document).ajaxError(function (event, jqXHR, ajaxSettings, thrownError) {
            if (jqXHR.status === 401) {
                // 如果是登入或刷新 API 本身報 401，不執行全域重導，交由該函式自己處理
                if (ajaxSettings.url.includes('/Auth/login') || ajaxSettings.url.includes('/Auth/refresh')) {
                    return;
                }
                console.error('[ARM Auth] API 回應未授權 (401)，重導至登入頁。');
                ArmAuthManager.logout(true);
            }
            else if (jqXHR.status === 403) {
                console.warn('[ARM Auth] API 拒絕存取 (403)，請確認使用者具備對應角色。');
                // 可在此處整合 Bootstrap 5 的 Toast 通知使用者權限不足
            }
        });
    };

    // ==========================================
    // 3. 公開介面 (Public API)
    // ==========================================
    return {
        init: function () {
            _loadFromStorage();
            _setupGlobalAjaxInterceptor();

            if (this.isAuthenticated()) {
                _startHeartbeat();
                console.log('ArmAuthManager 啟動，目前為已登入狀態。');
            }
        },

        // 登入成功後，儲存 UI 中繼資料並啟動心跳
        setSession: function (response) {
            if (!response || !response.userId) return;

            sessionMeta = {
                userId: response.userId,
                username: response.username,
                roleIds: response.roleIds,
                accessExpiresAt: new Date(response.accessExpiresAt).getTime()
            };

            sessionStorage.setItem(SESSION_KEY, JSON.stringify(sessionMeta));
            _startHeartbeat();
        },

        isAuthenticated: function () {
            if (!sessionMeta || !sessionMeta.accessExpiresAt) return false;
            return Date.now() < sessionMeta.accessExpiresAt;
        },

        getUserInfo: function () {
            return sessionMeta;
        },

        logout: function (forceRedirect = false) {
            // 呼叫後端清除 Cookie 與 KeyDB 狀態
            $.ajax({
                url: `${getBaseUrl()}/api/v1/Auth/logout`,
                type: 'POST',
                complete: function () {
                    _clearSession();
                    if (forceRedirect) {
                        const returnUrl = encodeURIComponent(window.location.pathname + window.location.search);
                        window.location.replace(`/Account/Login?returnUrl=${returnUrl}`);
                    }
                }
            });
        }
    };
})(jQuery);

$(document).ready(function() {
    ArmAuthManager.init();
});