// Version-pinned official Webots viewer. Physics/controllers run on the server.
const view = document.querySelector('webots-view');
const message = document.getElementById('message');
function fail(error) {
  clearTimeout(deadline);
  message.textContent = String(error);
  parent.postMessage({type:'smartfleet-simulation-error',message:String(error)}, location.origin);
}
const deadline = setTimeout(() => fail('Webots 3D did not respond within 45 seconds. Check simulator health and retry.'), 45000);
try {
  const [, {default: MeshLoader}, {default: ImageLoader}] = await Promise.all([
    import('https://cyberbotics.com/wwi/R2025a/WebotsView.js'),
    import('https://cyberbotics.com/wwi/R2025a/MeshLoader.js'),
    import('https://cyberbotics.com/wwi/R2025a/ImageLoader.js')
  ]);
  // R2025a broadcast mode skips updateWorldList(), which otherwise initializes mesh paths.
  MeshLoader.currentWorld = 'worlds/smartfleet.wbt';
  MeshLoader.branch = ImageLoader.branch = 'R2025a';
  for (const flag of ['showIde','showPlay','showQuit','showReload','showReset','showRobotWindow','showStep','showTerminal','showWorldSelection','showRun']) view[flag] = false;
  view.onready = () => { clearTimeout(deadline); message.textContent = ''; view.hideToolbar(); };
  view.ondisconnect = () => fail('Webots disconnected. Reconnect from SmartFleet; if robot reports are stale, check the simulator.');
  view.onerror = error => fail('Webots viewer unavailable: ' + String(error));
  view.connect(location.origin.replace(/^http/,'ws') + '/api/v1/simulation/view', 'w3d', true, false, -1);
} catch (error) { fail('Cannot load the Webots viewer modules: ' + String(error)); }
