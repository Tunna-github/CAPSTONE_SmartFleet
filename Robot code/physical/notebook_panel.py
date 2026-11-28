"""Notebook-only status/preview panel. Does not authorize motion."""
import html
import threading
import time


def show(agent, existing=None):
    import ipywidgets as w
    from IPython.display import display
    if existing is None:
        camera=w.Image(format='jpeg',width=640,height=480)
        status=w.HTML()
        stop=w.Button(description='STOP controller',button_style='danger',icon='stop')
        display(w.VBox([status,camera,stop]))
    else:
        status,camera,stop=existing
        # Rebind our existing panel after a scoped controller reload in Jupyter.
        for handler in list(stop._click_handlers.callbacks):
            stop.on_click(handler,remove=True)
    stop.on_click(lambda _:agent.close())
    def update():
        while True:
            try:
                a=agent.agent
                sample=agent.io.sample()
                line=sample.get('line',{})
                ids=', '.join(str(x) for x in sample.get('markers',{})) or 'none'
                error=agent.io.fault or a.error or ''
                warning=getattr(a,'communication_warning',None) or ''
                status.value=('<h3>JetRacer '+html.escape(a.code)+'</h3><p>MQTT: '+('connected' if a.connected else 'disconnected')+
                    ' · Backend READY: '+str(a.ready)+' · State: <b>'+html.escape(a.state)+'</b></p><p>Visible ArUco: <b>'+ids+
                    '</b> · Last checkpoint: '+str(a.current)+' · Line: '+('visible' if line.get('visible') else 'not visible')+
                    ' · Battery: '+str(sample.get('batteryPercent','?'))+'%</p><p>Task: '+str(a.route.get('taskId') if a.route else 'waiting for next task')+
                    '</p><p style="color:#dc2626">'+html.escape(error)+'</p><p style="color:#b45309">'+html.escape(warning)+'</p><p>Create/assign a MANUAL task on SmartFleet. Use web Pause to keep the same task; STOP controller ends this controller session.</p>')
                preview=getattr(agent.io,'preview_jpeg',b'')
                if preview:camera.value=preview
                if not agent.running:break
                time.sleep(.5)
            except Exception as ex:
                status.value='<p>'+html.escape(str(ex))+'</p>'
                break
    thread=threading.Thread(target=update)
    thread.daemon=True
    thread.start()
    return status,camera,stop
