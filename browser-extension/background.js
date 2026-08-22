// Must match the host name registered by install/register-native-host.ps1
// (see that script's $hostName and the .json host manifest it writes).
const NATIVE_HOST_NAME = "com.employeeagent.nativehost";

function reportUrl(tab) {
  if (!tab || !tab.url || !/^https?:\/\//i.test(tab.url)) return;

  const message = {
    url: tab.url,
    title: tab.title || "",
    timestampUtc: new Date().toISOString()
  };

  // One-shot sendNativeMessage (spawns the native host per call) instead of a
  // persistent connectNative() port - simpler and avoids MV3 service-worker
  // lifecycle issues (the worker can be suspended between navigations, which
  // would silently drop a long-lived port).
  chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, message, () => {
    if (chrome.runtime.lastError) {
      // Native host not installed/registered on this machine - fail silently.
      // Domain-level tracking in the main agent (window-title regex) still
      // covers this window even without the extension/host present.
      console.debug("EmployeeAgent native host unavailable:", chrome.runtime.lastError.message);
    }
  });
}

chrome.tabs.onUpdated.addListener((_tabId, changeInfo, tab) => {
  if (changeInfo.status === "complete") {
    reportUrl(tab);
  }
});

chrome.tabs.onActivated.addListener(({ tabId }) => {
  chrome.tabs.get(tabId, (tab) => reportUrl(tab));
});
