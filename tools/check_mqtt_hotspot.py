"""Non-motion Docker <-> JetRacer MQTT roundtrip, using the robot's existing paho.

Requires paramiko on the checking PC, never installs anything on the robot.
SSH host keys must already be trusted. Does not open or execute a notebook.
"""
import argparse
import getpass
import json
import os
from pathlib import Path
import shlex
import subprocess
import time
import uuid
from aiven_support import ROOT, read_env


def responder_source(host, port, topic, nonce):
    # Python 3.6 / paho 1.x compatible. No imports of robot motion/hardware code.
    return '''import threading, json
import paho.mqtt.client as mqtt
host,port,topic,nonce = {settings}
done=threading.Event()
sent=[]
c=mqtt.Client(client_id="sf-hotspot-diagnostic-"+nonce[:12])
def connected(client,userdata,flags,rc):
 if rc!=0: print("LOGIN_FAILED",flush=True);done.set();return
 client.subscribe(topic+"/probe",qos=1)
def subscribed(client,userdata,mid,qos):
 print("DIAGNOSTIC_READY",flush=True)
def message(client,userdata,msg):
 if msg.topic==topic+"/probe" and msg.payload.decode()==nonce and not msg.retain:
  sent.append(client.publish(topic+"/reply",nonce,qos=1,retain=False))
  done.set()
c.on_connect=connected;c.on_subscribe=subscribed;c.on_message=message
try:
 c.connect(host,port,keepalive=30);c.loop_start()
 if not done.wait(30) or not sent: raise RuntimeError("No diagnostic probe received")
 sent[0].wait_for_publish()
 print("DIAGNOSTIC_REPLIED",flush=True)
finally:
 c.disconnect();c.loop_stop()
'''.format(settings=repr((host, port, topic, nonce)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--robot-host', default='192.168.137.168')
    parser.add_argument('--ssh-user', default='jetson')
    parser.add_argument('--known-hosts', type=Path, help='Optional existing trusted SSH host key file')
    args = parser.parse_args()
    import paramiko
    config = read_env()
    host = config.get('MQTT_HOTSPOT_ADDRESS', '192.168.137.1')
    port = int(config.get('MQTT_HOTSPOT_PORT', '1883'))
    nonce = uuid.uuid4().hex
    topic = 'smartfleet/diagnostics/hotspot/' + nonce
    password = os.environ.get('SMARTFLEET_DIAGNOSTIC_SSH_PASSWORD') or getpass.getpass('JetRacer SSH password: ')
    ssh = paramiko.SSHClient()
    ssh.load_system_host_keys()
    if args.known_hosts:
        ssh.load_host_keys(str(args.known_hosts))
    ssh.set_missing_host_key_policy(paramiko.RejectPolicy())
    subscriber = None
    try:
        ssh.connect(args.robot_host, username=args.ssh_user, password=password,
                    look_for_keys=False, allow_agent=False, timeout=8)
        _, output, errors = ssh.exec_command('python3 -u -c ' + shlex.quote(responder_source(host, port, topic, nonce)), timeout=40)
        if output.readline().strip() != 'DIAGNOSTIC_READY':
            raise RuntimeError('Robot could not connect/subscribe to the published Docker broker: ' + errors.read().decode()[-300:])
        # Start the reply subscriber before publishing; no command/status topics.
        subscriber = subprocess.Popen(['docker','compose','exec','-T','mqtt','mosquitto_sub',
            '-h','localhost','-t',topic+'/reply','-q','1','-C','1','-W','15'],cwd=ROOT,
            stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        time.sleep(.5)
        subprocess.run(['docker','compose','exec','-T','mqtt','mosquitto_pub',
            '-h','localhost','-t',topic+'/probe','-q','1','-m',nonce],cwd=ROOT,check=True)
        result,error=subscriber.communicate(timeout=20)
        if subscriber.returncode or result.strip()!=nonce:
            raise RuntimeError('Docker did not receive the exact live JetRacer reply: '+error[-300:])
        remaining=output.read().decode()
        if output.channel.recv_exit_status()!=0 or 'DIAGNOSTIC_REPLIED' not in remaining:
            raise RuntimeError('Robot responder failed: '+errors.read().decode()[-300:])
        print(json.dumps(dict(passed=True,robotHost=args.robot_host,broker=host+':'+str(port),
            dockerToRobot=True,robotToDocker=True,notebooksExecuted=False,motorCommandsSent=False)))
    finally:
        if subscriber is not None and subscriber.poll() is None:
            subscriber.terminate();subscriber.communicate(timeout=5)
        ssh.close()


if __name__=='__main__':
    main()
