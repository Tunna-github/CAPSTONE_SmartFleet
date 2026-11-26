// Version-pinned official Webots viewer. Physics/controllers run on the server.
const view = document.querySelector('webots-view');
const message = document.getElementById('message');
let failed = false;
function fail(error, recoverable = false) {
  if (failed) return;
  failed = true;
  clearTimeout(deadline);
  message.textContent = String(error);
  parent.postMessage({type:'smartfleet-simulation-error',message:String(error),recoverable}, location.origin);
}
const deadline = setTimeout(() => fail('Webots 3D did not respond within 45 seconds. Check simulator health and retry.'), 45000);
try {
  const [, {default: MeshLoader}, {default: ImageLoader}, {default: Stream}] = await Promise.all([
    import('https://cyberbotics.com/wwi/R2025a/WebotsView.js'),
    import('https://cyberbotics.com/wwi/R2025a/MeshLoader.js'),
    import('https://cyberbotics.com/wwi/R2025a/ImageLoader.js'),
    import('https://cyberbotics.com/wwi/R2025a/Stream.js')
  ]);
  // R2025a's default close handler prompts to reset/reload its page, even in
  // broadcast mode. This observer iframe delegates recovery to SmartFleet;
  // reconnecting the viewer must never offer simulation controls.
  const connectStream = Stream.prototype.connect;
  Stream.prototype.connect = function (...args) {
    connectStream.apply(this, args);
    const disconnected = detail => {
      if (failed) return;
      fail(`3D viewing connection lost (${detail}). Robot tasks continue independently.`, true);
      this.view.destroyWorld();
    };
    this.socket.onclose = event => disconnected(`code ${event.code}`);
    this.socket.onerror = () => disconnected('network error');
  };
  // R2025a broadcast mode skips updateWorldList(), which otherwise initializes mesh paths.
  MeshLoader.currentWorld = 'worlds/smartfleet.wbt';
  MeshLoader.branch = ImageLoader.branch = 'R2025a';
  for (const flag of ['showIde','showPlay','showQuit','showReload','showReset','showRobotWindow','showStep','showTerminal','showWorldSelection','showRun']) view[flag] = false;
  view.onready = () => { if (failed) return; clearTimeout(deadline); message.textContent = ''; view.hideToolbar();
    parent.postMessage({type:'smartfleet-simulation-ready'}, location.origin); };
  view.ondisconnect = () => fail('Webots disconnected. Reconnect from SmartFleet; if robot reports are stale, check the simulator.');
  view.onerror = error => fail('Webots viewer unavailable: ' + String(error));
  view.connect(location.origin.replace(/^http/,'ws') + '/api/v1/simulation/view', 'w3d', true, false, -1);
} catch (error) { fail('Cannot load the Webots viewer modules: ' + String(error)); }
// A kept-alive viewer can become visible again without a document reload/socket reconnect.
window.addEventListener('message', event => {
  if (event.origin === location.origin && event.source === parent && event.data?.type === 'smartfleet-viewer-resize')
    window.dispatchEvent(new Event('resize'));
});
