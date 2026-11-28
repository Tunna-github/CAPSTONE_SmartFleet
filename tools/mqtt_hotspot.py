"""Publish the existing Docker broker on the hotspot, without starting robot control."""
import argparse
import ipaddress
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import time
from aiven_support import ROOT, read_env, update_env

OVERLAY = 'compose.mqtt-hotspot.yaml'


def compose_files(config):
    separator = config.get('COMPOSE_PATH_SEPARATOR', os.pathsep)
    files = [f for f in config.get('COMPOSE_FILE', 'compose.yaml').split(separator) if f]
    if OVERLAY not in files:
        files.append(OVERLAY)
    return files


def keep_hotspot_overlay(files, config):
    old = config.get('COMPOSE_FILE', '').split(config.get('COMPOSE_PATH_SEPARATOR', os.pathsep))
    return list(files) + ([OVERLAY] if OVERLAY in old and OVERLAY not in files else [])


def endpoint(address, port):
    parsed = ipaddress.ip_address(address)
    if parsed.version != 4 or not parsed.is_private or parsed.is_unspecified or parsed.is_loopback or parsed.is_link_local:
        raise ValueError('Use the actual private IPv4 address of the hotspot adapter')
    if not 1 <= port <= 65535:
        raise ValueError('Invalid host TCP port')
    return str(parsed), port


def run(command, **kwargs):
    return subprocess.run(command, cwd=ROOT, check=True, **kwargs)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--enable', action='store_true', help='Save optional Compose binding and start only mqtt; otherwise inspect')
    parser.add_argument('--address')
    parser.add_argument('--port', type=int)
    args = parser.parse_args()
    config = read_env()
    address, port = endpoint(args.address or config.get('MQTT_HOTSPOT_ADDRESS','192.168.137.1'),
                             args.port if args.port is not None else int(config.get('MQTT_HOTSPOT_PORT',1883)))
    files = compose_files(config)
    command = ['docker', 'compose']
    for filename in files:
        command += ['-f', filename]
    env = os.environ.copy()
    env.update(MQTT_HOTSPOT_ADDRESS=address, MQTT_HOTSPOT_PORT=str(port))
    # Parse privately: never print Compose JSON, which contains DB/app secrets.
    resolved = json.loads(subprocess.check_output(command + ['config', '--format', 'json'], cwd=ROOT, env=env))
    assert resolved['services']['backend']['environment']['Mqtt__Host'] == 'mqtt'
    bindings = resolved['services']['mqtt']['ports']
    assert any(p.get('host_ip') == address and int(p['published']) == port and p['target'] == 1883 for p in bindings)
    # Binding a temporary port proves the hotspot exists, without modifying networking.
    try:
        with socket.socket() as probe:
            probe.bind((address, 0))
    except OSError:
        raise SystemExit('Hotspot IP is not active. Turn on Mobile hotspot first; no configuration was changed.')
    print('Backend endpoint remains mqtt:1883; JetRacer endpoint is ' + address + ':' + str(port))
    print('Compose files: ' + ', '.join(files))
    if not args.enable:
        print('No changes made. Use --enable once; subsequent docker compose up -d preserves this binding.')
        return
    existing = subprocess.check_output(['docker', 'compose', 'ps', '-q', 'mqtt'], cwd=ROOT, text=True).strip()
    already_published = False
    if existing:
        published = json.loads(subprocess.check_output(['docker', 'inspect', '--format', '{{json .NetworkSettings.Ports}}', existing], text=True)) or {}
        already_published = any(p.get('HostIp') == address and p.get('HostPort') == str(port)
                                for p in published.get('1883/tcp') or [])
    running = subprocess.check_output(['docker', 'compose', 'ps', '--status', 'running', '--services'], cwd=ROOT, text=True).splitlines()
    if not already_published:
        if {'backend', 'webots'} & set(running):
            raise SystemExit('The fleet is running. Publishing requires broker recreation: finish/safely stop it first. Nothing was stopped.')
        try:
            with socket.socket() as probe:
                probe.bind((address, port))
        except OSError:
            raise SystemExit('Host port is already occupied; no process was stopped. Inspect the owner before retrying.')
    private = Path(os.environ.get('LOCALAPPDATA', Path.home() / '.local/share')) / 'SmartFleet/mqtt-hotspot'
    private.mkdir(parents=True, exist_ok=True)
    saved = private / ('env-before-hotspot-' + time.strftime('%Y%m%d-%H%M%S'))
    shutil.copy2(ROOT / '.env', saved)
    update_env(dict(COMPOSE_FILE=config.get('COMPOSE_PATH_SEPARATOR', os.pathsep).join(files),
                    MQTT_HOTSPOT_ADDRESS=address, MQTT_HOTSPOT_PORT=port))
    run(['docker', 'compose', 'up', '-d', '--no-deps', 'mqtt'])
    print('Started only the existing MQTT broker. Backend, Webots, notebooks and database were not started/modified.')
    print('Private .env backup retained. Binding persists for future docker compose up -d.')


if __name__ == '__main__':
    main()
