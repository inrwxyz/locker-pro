// ==========================================
// Locker Key Companion App Logic (Lean & Fast)
// Pure Local WiFi Direct Biometric Authentication
// ==========================================

const AppState = {
  pairedPC: null, // { name, ip, pairedAt } (secret stays in native Android Keystore/SharedPreferences)
  vibrateEnabled: true,
  pendingAuthTarget: null,
  pendingActionType: 'unlock',
  pendingNeedsReprompt: false
};

// Initialize on Load
document.addEventListener('DOMContentLoaded', () => {
  loadStoredSettings();
  setupTabNavigation();
  setupDashboardActions();
  setupSettingsSection();
  updatePCUI();

  // Auto-sync check with paired PC on launch (syncs unpair state if PC unpaired while phone was offline)
  setTimeout(() => {
    if (AppState.pairedPC && AppState.pairedPC.ip && window.Android && window.Android.checkPairStatus) {
      window.Android.checkPairStatus(AppState.pairedPC.ip, 58820, '_onStartupAutoCheck');
    }
  }, 400);
});

// Load settings from native storage & metadata cache (never store secret in localStorage)
function loadStoredSettings() {
  if (window.Android && window.Android.getPairedPCFromNative) {
    try {
      const nativeRaw = window.Android.getPairedPCFromNative();
      if (nativeRaw && nativeRaw !== 'null') {
        const parsed = JSON.parse(nativeRaw);
        AppState.pairedPC = { name: parsed.name || 'Locker PC', ip: parsed.ip || '', pairedAt: parsed.pairedAt || Date.now() };
      }
    } catch (_) {}
  }

  // Purge any legacy localStorage copy that might have held a plaintext secret
  try {
    const raw = localStorage.getItem('locker_paired_pc');
    if (raw) {
      const old = JSON.parse(raw);
      if (old && old.secret) {
        if (AppState.pairedPC) {
          localStorage.setItem('locker_paired_pc', JSON.stringify(AppState.pairedPC));
        } else {
          localStorage.removeItem('locker_paired_pc');
        }
      } else if (!AppState.pairedPC && old) {
        AppState.pairedPC = old;
      }
    }
  } catch (_) {}

  const vib = localStorage.getItem('locker_vibrate');
  AppState.vibrateEnabled = vib === null ? true : (vib === 'true');
  const toggleVib = document.getElementById('toggle-vibrate');
  if (toggleVib) toggleVib.checked = AppState.vibrateEnabled;

  // Device model from Android
  if (window.Android && window.Android.getDeviceName) {
    const model = window.Android.getDeviceName();
    const modelEl = document.getElementById('device-model-name');
    if (modelEl) modelEl.textContent = model;
  }
}

// Tab Switching
function setupTabNavigation() {
  const navBtns = document.querySelectorAll('.nav-btn');
  navBtns.forEach(btn => {
    btn.addEventListener('click', () => {
      const targetId = btn.getAttribute('data-tab');
      navBtns.forEach(b => b.classList.remove('active'));
      document.querySelectorAll('.tab-pane').forEach(p => p.classList.remove('active'));

      btn.classList.add('active');
      const targetPane = document.getElementById(targetId);
      if (targetPane) targetPane.classList.add('active');
    });
  });
}

// Dashboard Actions
function setupDashboardActions() {
  // Pair from Dashboard
  const btnScanQr = document.getElementById('btn-scan-qr');
  if (btnScanQr) {
    btnScanQr.addEventListener('click', () => {
      showToast('Opening camera scanner...');
      if (window.Android && window.Android.startQrScan) {
        window.Android.startQrScan();
      } else {
        showToast('Camera scanner unavailable');
      }
    });
  }

  // Disconnect / Unpair
  const btnUnpair = document.getElementById('btn-unpair-dash');
  if (btnUnpair) {
    btnUnpair.addEventListener('click', () => {
      const pcIp = AppState.pairedPC?.ip || '';
      AppState.pairedPC = null;
      localStorage.removeItem('locker_paired_pc');
      if (window.Android && window.Android.unpairFromPC) {
        window.Android.unpairFromPC(pcIp, 58820);
      } else if (window.Android && window.Android.clearPairedPCFromNative) {
        window.Android.clearPairedPCFromNative();
      }
      updatePCUI();
      showToast('PC Unpaired.');
    });
  }

  // Approve Incoming Auth Button
  document.getElementById('btn-approve-auth')?.addEventListener('click', () => {
    if (!AppState.pairedPC && window.Android && window.Android.getPairedPCFromNative) {
      try {
        const raw = window.Android.getPairedPCFromNative();
        if (raw && raw !== 'null') AppState.pairedPC = JSON.parse(raw);
      } catch (_) {}
    }

    const pendingNative = window.Android?.getPendingAuthFromNative?.() || '';
    if (AppState.pendingNeedsReprompt && !pendingNative && window.Android?.requestPcReprompt && AppState.pairedPC) {
      showToast('Re-syncing unlock request with PC...');
      window.Android.requestPcReprompt(AppState.pairedPC.ip, 58820, '_onRepromptResult');
      return;
    }

    const target = AppState.pendingAuthTarget || 'Protected Item';
    const actionType = AppState.pendingActionType || 'unlock';
    const isLock = actionType === 'lock';
    triggerBiometric(
      isLock ? 'Locker Lock Request' : 'Locker Authorization',
      (isLock ? 'Confirm locking: ' : 'Authorize unlock for: ') + target,
      (approved, authType) => {
        if (approved) {
          if (AppState.vibrateEnabled && window.Android) window.Android.vibrate(120);
          if (window.Android && window.Android.sendBiometricApproval && AppState.pairedPC) {
            window.Android.sendBiometricApproval(AppState.pairedPC.ip, 58820, '', target, authType || 'fingerprint'); // no-op natively; the signed approval is sent right after the biometric prompt
          }
          showToast(isLock ? ('Locked! ' + target + ' locked on PC.') : ('Authorized! ' + target + ' unlocked on PC.'));
          hideIncomingAuthBanner();
        }
      }
    );
  });

  // Test Ping Button
  document.getElementById('btn-ping-pc')?.addEventListener('click', () => {
    if (!AppState.pairedPC) {
      showToast('No PC Paired');
      return;
    }
    showToast('Testing connection with PC...');
    if (window.Android && window.Android.pingPC) {
      window.Android.pingPC(AppState.pairedPC.ip, 58820, 'onPingResult');
    }
  });
}

// Global Callback for Ping
window.onPingResult = function(success, data, error) {
  if (success) {
    showToast('PC Online & Connected! (Port 58820 OK)');
  } else {
    showToast('PC unreachable: ' + (error || 'Ensure PC and Phone are on same Wi-Fi'));
  }
};

// Startup Auto-check Callback
// NOTE: the PC no longer sends the shared secret in a ping reply (that let anyone on the Wi-Fi steal it).
// The secret is only handed over once, during PIN pairing. If this phone lost it, it has to pair again.
window._onStartupAutoCheck = function(success, data, error) {
  window._onResumeSync(success, data, error);
};

window._onResumeSync = function(success, data, error) {
  const nativePending = window.Android?.getPendingAuthFromNative?.() || '';
  if (nativePending) {
    try {
      const p = JSON.parse(nativePending);
      if (p?.target) {
        window.handleIncomingAuthPrompt(p.target, p.actionType || 'unlock', false);
        return;
      }
    } catch (_) {}
  }

  if (!success || !data) return;
  try {
    const parsed = JSON.parse(data);
    if (parsed && parsed.pendingTarget) {
      window.handleIncomingAuthPrompt(parsed.pendingTarget, parsed.pendingAction || 'unlock', true);
    } else if (AppState.pendingNeedsReprompt) {
      window.hideIncomingAuthBanner();
    }
  } catch (_) {}
};

window._onRepromptResult = function(success, data, error) {
  if (!success) {
    showToast('Could not reach PC: ' + (error || 'Ensure PC prompt is open'));
  }
};

// Biometric Prompt Trigger
function triggerBiometric(title, subtitle, callback) {
  if (window.Android && window.Android.authenticateBiometric) {
    const callbackName = '_bioCallback_' + Math.random().toString(36).substring(2, 9);
    window[callbackName] = function(approved, authType, error) {
      delete window[callbackName];
      callback(approved, authType, error);
    };
    window.Android.authenticateBiometric(title, subtitle, callbackName);
  } else {
    // Web test fallback
    const ok = confirm(title + '\n' + subtitle + '\n\nSimulate Biometric Approval?');
    callback(ok, 'fingerprint', null);
  }
}

// Incoming Auth Prompts from PC
window.handleIncomingAuthPrompt = function(target, actionType, needsReprompt) {
  AppState.pendingAuthTarget = target;
  AppState.pendingActionType = actionType || 'unlock';
  AppState.pendingNeedsReprompt = !!needsReprompt;
  const banner = document.getElementById('incoming-auth-card');
  const title = document.getElementById('incoming-target-title');
  const isLock = AppState.pendingActionType === 'lock';
  const approveBtn = document.getElementById('btn-approve-auth');
  if (title) title.textContent = (isLock ? '🔒 Lock: ' : '') + (target || 'Unlock Request');
  if (approveBtn) approveBtn.textContent = isLock ? 'Confirm Lock' : 'Approve';
  if (banner) banner.classList.remove('hidden');
};

window.hideIncomingAuthBanner = function() {
  const banner = document.getElementById('incoming-auth-card');
  if (banner) banner.classList.add('hidden');
  AppState.pendingAuthTarget = null;
  AppState.pendingNeedsReprompt = false;
};

// Helper: Save Paired PC Metadata (never persists pairing secret in localStorage)
function savePairedPC(name, ip) {
  AppState.pairedPC = {
    name: name || 'Locker PC',
    ip: ip || '',
    pairedAt: Date.now()
  };
  try {
    localStorage.setItem('locker_paired_pc', JSON.stringify(AppState.pairedPC));
  } catch (_) {}

  updatePCUI();
}

function updatePCUI() {
  const pairedCard = document.getElementById('paired-hero-card');
  const nameEl = document.getElementById('pc-name-display');
  const ipEl = document.getElementById('pc-ip-display');

  if (AppState.pairedPC) {
    document.body.classList.remove('unpaired');
    document.body.classList.add('paired');
    if (pairedCard) pairedCard.classList.remove('hidden');
    if (nameEl) nameEl.textContent = AppState.pairedPC.name || 'Locker PC';
    if (ipEl) ipEl.textContent = (AppState.pairedPC.ip || '192.168.0.100') + ' \u2022 Paired & Active';
  } else {
    // Not paired: whole app resets to the full-screen scan page
    document.body.classList.remove('paired');
    document.body.classList.add('unpaired');
    if (pairedCard) pairedCard.classList.add('hidden');
    hideIncomingAuthBanner();
    // back to the Key tab so next pairing starts clean
    document.querySelectorAll('.nav-btn').forEach(b => b.classList.toggle('active', b.getAttribute('data-tab') === 'tab-dashboard'));
    document.querySelectorAll('.tab-pane').forEach(p => p.classList.toggle('active', p.id === 'tab-dashboard'));
  }
}

// Settings Section
function setupSettingsSection() {
  document.getElementById('toggle-vibrate')?.addEventListener('change', (e) => {
    AppState.vibrateEnabled = e.target.checked;
    localStorage.setItem('locker_vibrate', AppState.vibrateEnabled);
    if (AppState.vibrateEnabled && window.Android) window.Android.vibrate(50);
  });
}

// Toast
function showToast(msg) {
  const t = document.getElementById('toast');
  if (!t) return;
  t.textContent = msg;
  t.classList.remove('hidden');
  clearTimeout(t._timer);
  t._timer = setTimeout(() => {
    t.classList.add('hidden');
  }, 2500);
}

window.onPairSuccess = function(pcName, ip) {
  showToast('Paired with ' + pcName + '!');
  if (window.Android && window.Android.getPairedPCFromNative) {
    try {
      const nativeRaw = window.Android.getPairedPCFromNative();
      if (nativeRaw && nativeRaw !== 'null') {
        const parsed = JSON.parse(nativeRaw);
        savePairedPC(parsed.name || pcName, parsed.ip || ip);
        return;
      }
    } catch (_) {}
  }
  savePairedPC(pcName, ip);
};

window.onPcUnpaired = function() {
  AppState.pairedPC = null;
  try { localStorage.removeItem('locker_paired_pc'); } catch (_) {}
  updatePCUI();
  showToast('Unpaired from PC');
};

window.onPairError = function(error) {
  showToast('Pairing failed: ' + (error || 'Unknown error'));
};

window.onQrScanCancelled = function() {
  showToast('QR Scan Cancelled');
};