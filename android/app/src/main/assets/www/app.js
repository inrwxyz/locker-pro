// ==========================================
// Locker Key Companion App Logic (Lean & Fast)
// Pure Local WiFi Direct Biometric Authentication
// ==========================================

const AppState = {
  pairedPC: null, // { name, ip, pairedAt } (secret stays in native Android Keystore/SharedPreferences)
  vibrateEnabled: true,
  pendingAuthTarget: null
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
  // Toggle pair accordion
  const btnTogglePair = document.getElementById('btn-toggle-pair-box');
  const pairInputsBox = document.getElementById('pair-inputs-box');
  if (btnTogglePair && pairInputsBox) {
    btnTogglePair.addEventListener('click', () => {
      const isHidden = pairInputsBox.classList.contains('hidden');
      if (isHidden) {
        pairInputsBox.classList.remove('hidden');
        btnTogglePair.textContent = 'Hide Connection Box';
      } else {
        pairInputsBox.classList.add('hidden');
        btnTogglePair.textContent = '+ Connect Another PC / Change IP';
      }
    });
  }

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

  // Sync State with PC
  const btnSyncState = document.getElementById('btn-sync-state');
  if (btnSyncState) {
    btnSyncState.addEventListener('click', () => {
      const ip = (document.getElementById('dash-pc-ip')?.value || '').trim();
      if (!ip) {
        showToast('Enter PC IP Address first');
        return;
      }
      showToast('Syncing with PC at ' + ip + '...');
      if (window.Android && window.Android.checkPairStatus) {
        window.Android.checkPairStatus(ip, 58820, '_onManualSyncResult');
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

// Global Pair Request
function doSendPairRequest(ip, pin) {
  if (window.Android && window.Android.sendPairRequest) {
    const deviceName = (window.Android.getDeviceName ? window.Android.getDeviceName() : 'Android Companion');
    window.Android.sendPairRequest(ip, 58820, pin, deviceName, 'onPairResult');
  } else {
    showToast('Native pairing not supported');
  }
}

// Global Callback for Pairing Result
window.onPairResult = function(success, data, error) {
  if (success && data) {
    try {
      if (typeof data === 'string') data = data.replace(/^\uFEFF/, '').trim();
      const res = (typeof data === 'string') ? JSON.parse(data) : data;
      if (res.status === 'paired' && res.secret) {
        const ip = document.getElementById('dash-pc-ip')?.value?.trim() || AppState.pairedPC?.ip || '';
        savePairedPC(res.pcName || 'Locker PC', ip, res.secret);
        showToast('Successfully paired with ' + (res.pcName || 'PC') + '!');
        updatePCUI();
      } else {
        showToast('Pairing: ' + (res.status || 'unknown status'));
      }
    } catch (e) {
      showToast('Pairing response error: ' + (e.message || 'invalid format'));
    }
  } else {
    showToast('Pairing failed: ' + (error || 'PIN incorrect or PC unreachable'));
  }
};

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
  // nothing to sync: the native side keeps the stored secret and refreshes the PC name/IP itself
};

// Check-before-pair Callback
window._onCheckPairBeforePair = function(success, data, error) {
  if (!success) {
    showToast('PC unreachable: ' + (error || 'Check PC IP'));
    return;
  }
  try {
    const res = (typeof data === 'string') ? JSON.parse(data) : data;
    if (res.paired && res.authenticated) {
      showToast('Already paired with ' + (res.pcName || 'PC'));
    } else {
      showToast('Enter the 6-digit PIN shown on PC (Pair New Phone)');
    }
  } catch (_) {
    showToast('Enter the 6-digit PIN shown on PC');
  }
};

// Manual Sync Callback
window._onManualSyncResult = function(success, data, error) {
  if (success && data) {
    try {
      const res = (typeof data === 'string') ? JSON.parse(data) : data;
      if (res.paired && res.authenticated) {
        showToast('Synced! Paired with ' + (res.pcName || 'PC'));
      } else if (res.paired) {
        showToast('PC is paired, but not verified for this phone. Use "Pair New Phone" on the PC and enter the PIN.');
      } else {
        showToast('PC is online but not paired with this phone. Enter PIN.');
      }
    } catch (_) {
      showToast('Sync completed');
    }
  } else {
    showToast('Sync failed: ' + (error || 'Check PC IP'));
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
window.handleIncomingAuthPrompt = function(target, actionType) {
  AppState.pendingAuthTarget = target;
  AppState.pendingActionType = actionType || 'unlock';
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

function updateHero() {
  const hero = document.getElementById('hero');
  const t = document.getElementById('hero-title');
  const sub = document.getElementById('hero-sub');
  if (!hero) return;
  if (AppState.pairedPC) {
    hero.classList.add('connected');
    if (t) t.textContent = AppState.pairedPC.name || 'Locker PC';
    if (sub) sub.textContent = 'Linked';
  } else {
    hero.classList.remove('connected');
    if (t) t.textContent = 'Not connected';
    if (sub) sub.textContent = 'Scan the QR code on your PC to pair';
  }
}

function updatePCUI() {
  updateHero();
  const pairedCard = document.getElementById('paired-hero-card');
  const nameEl = document.getElementById('pc-name-display');
  const ipEl = document.getElementById('pc-ip-display');
  const btnTogglePair = document.getElementById('btn-toggle-pair-box');
  const pairInputsBox = document.getElementById('pair-inputs-box');
  
  if (AppState.pairedPC) {
    if (pairedCard) pairedCard.classList.remove('hidden');
    if (nameEl) nameEl.textContent = AppState.pairedPC.name || 'Locker PC';
    if (ipEl) ipEl.textContent = (AppState.pairedPC.ip || '192.168.0.100') + ' • Paired & Active';
    
    // Strict requirement: HIDE pairing inputs when connected!
    if (pairInputsBox) pairInputsBox.classList.add('hidden');
    if (btnTogglePair) {
      btnTogglePair.classList.remove('hidden');
      btnTogglePair.textContent = '+ Connect Another PC / Change IP';
    }
  } else {
    if (pairedCard) pairedCard.classList.add('hidden');
    // Not paired: show pairing box directly
    if (pairInputsBox) pairInputsBox.classList.remove('hidden');
    if (btnTogglePair) btnTogglePair.classList.add('hidden');
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