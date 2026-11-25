// Version-pinned official Webots viewer. Physics/controllers run on the server.
import 'https://cyberbotics.com/wwi/R2025a/WebotsView.js';
import MeshLoader from 'https://cyberbotics.com/wwi/R2025a/MeshLoader.js';
import ImageLoader from 'https://cyberbotics.com/wwi/R2025a/ImageLoader.js';
// R2025a broadcast mode skips updateWorldList(), which otherwise initializes mesh paths.
MeshLoader.currentWorld = 'worlds/smartfleet.wbt';
MeshLoader.branch = ImageLoader.branch = 'R2025a';
const view = document.querySelector('webots-view');
const message = document.getElementById('message');
for (const flag of ['showIde','showPlay','showQuit','showReload','showReset','showRobotWindow','showStep','showTerminal','showWorldSelection','showRun']) view[flag] = false;
view.onready = () => { message.textContent = ''; view.hideToolbar(); };
view.ondisconnect = () => { message.textContent = 'Disconnected. Reconnect from SmartFleet.'; };
view.onerror = error => {
  message.textContent = 'Webots viewer unavailable. Reconnect from SmartFleet.';
  parent.postMessage({type:'smartfleet-simulation-error',message:String(error)}, location.origin);
};
view.connect(location.origin.replace(/^http/,'ws') + '/api/v1/simulation/view', 'w3d', true, false, -1);
