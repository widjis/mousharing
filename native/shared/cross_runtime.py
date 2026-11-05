"""Run actual Windows C# channel code against the Python oracle in both roles.

Uses only loopback sockets and public fixture keys. Does not access OS input or clipboard.
Build native/shared/dotnet first; requires a .NET 10 runtime and cryptography.
"""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
from protocol_reference import (
    MAX_FRAME_BYTES,PREFIX,ProtocolError,RecordCipher,base64_decode,base64_encode,
    derive_keys,frame,parse_frame,seal,unseal,validate_hello,validate_welcome,
)

ROOT=Path(__file__).resolve().parent
FIXTURE_PATH=ROOT/'fixtures.json'
FIXTURE=json.loads(FIXTURE_PATH.read_text())
KEY=bytes.fromhex(FIXTURE['keyHex'])
MASTER=FIXTURE['invitePayload']['id']
CLIENT='22222222-2222-4222-8222-222222222222'
CLIENT_TEXT='CROSS-RUNTIME CLIENT 👋\nline 2'
SERVER_TEXT='CROSS-RUNTIME SERVER ✅\nline 2'


def read(stream):
    return parse_frame(stream.readline(MAX_FRAME_BYTES+1))


def ensure(condition,message):
    if not condition:
        raise ProtocolError(message)


def complete(process):
    output,error=process.communicate(timeout=20)
    ensure(process.returncode==0,'C# peer failed: '+error.strip())
    print(output.strip())


def stop(process):
    if process.poll() is None:
        process.kill()
        process.communicate(timeout=5)


def python_client(command):
    with tempfile.TemporaryDirectory() as directory:
        port_file=Path(directory)/'port.txt'
        process=subprocess.Popen(command+['server',str(FIXTURE_PATH),str(port_file)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        try:
            deadline=time.monotonic()+15
            while not port_file.exists():
                if process.poll() is not None:
                    complete(process)
                    raise ProtocolError('C# server did not create listener')
                ensure(time.monotonic()<deadline,'C# listener startup timeout')
                time.sleep(0.02)
            port=int(port_file.read_text())
            with socket.create_connection(('127.0.0.1',port),timeout=10) as conn,conn.makefile('rb') as stream:
                plain=read(stream)
                ensure(plain.get('type')=='challenge' and plain.get('v')==1 and plain.get('id')==MASTER,'Untrusted C# master')
                challenge=base64_decode(plain['challenge'],32)
                nonce=os.urandom(32)
                hello={'type':'hello','v':1,'challenge':base64_encode(challenge),'clientNonce':base64_encode(nonce),'id':CLIENT,'name':'Python test client','clipboard':True}
                conn.sendall(frame(seal(KEY,hello,PREFIX+'hello',0)))
                welcome=validate_welcome(read(stream),KEY,challenge,nonce,MASTER)
                ensure(welcome.get('clipboard') is True,'C# clipboard capability not negotiated')
                keys=derive_keys(KEY,challenge,nonce)
                sending,receiving=RecordCipher(keys['c2s'],'c2s'),RecordCipher(keys['s2c'],'s2c')
                conn.sendall(frame(sending.encrypt({'type':'clipboard','text':CLIENT_TEXT,'clock':1,'origin':CLIENT})))
                message=receiving.decrypt(read(stream))
                ensure(message.get('type')=='clipboard' and message.get('text')==SERVER_TEXT,'C# master clipboard payload mismatch')
                conn.sendall(frame(sending.encrypt({'type':'ping'})))
                ensure(receiving.decrypt(read(stream)).get('type')=='pong','C# master pong missing')
            complete(process)
        finally:
            stop(process)


def python_master(command):
    with socket.create_server(('127.0.0.1',0)) as listener:
        listener.settimeout(15)
        port=listener.getsockname()[1]
        process=subprocess.Popen(command+['client',str(FIXTURE_PATH),str(port)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        try:
            conn,_=listener.accept()
            conn.settimeout(10)
            with conn,conn.makefile('rb') as stream:
                challenge=os.urandom(32)
                conn.sendall(frame({'type':'challenge','v':1,'challenge':base64_encode(challenge),'id':MASTER,'name':'Python test master'}))
                hello=validate_hello(read(stream),KEY,challenge,CLIENT)
                ensure(hello.get('clipboard') is True,'C# client did not request clipboard capability')
                nonce=base64_decode(hello['clientNonce'],32)
                welcome={'type':'welcome','v':1,'challenge':base64_encode(challenge),'clientNonce':base64_encode(nonce),'id':MASTER,'name':'Python test master','clipboard':True}
                conn.sendall(frame(seal(KEY,welcome,PREFIX+'welcome',0)))
                keys=derive_keys(KEY,challenge,nonce)
                sending,receiving=RecordCipher(keys['s2c'],'s2c'),RecordCipher(keys['c2s'],'c2s')
                message=receiving.decrypt(read(stream))
                ensure(message.get('type')=='clipboard' and message.get('text')==CLIENT_TEXT,'C# client clipboard payload mismatch')
                conn.sendall(frame(sending.encrypt({'type':'clipboard','text':SERVER_TEXT,'clock':2,'origin':MASTER})))
                ensure(receiving.decrypt(read(stream)).get('type')=='ping','C# client ping missing')
                conn.sendall(frame(sending.encrypt({'type':'pong'})))
            complete(process)
        finally:
            stop(process)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet',default='dotnet')
    parser.add_argument('--dll',type=Path,default=ROOT/'dotnet/bin/Release/net10.0/Mousharing.ProtocolChecks.dll')
    args=parser.parse_args()
    ensure(args.dll.is_file(),'Build the actual C# protocol-check console first')
    command=[args.dotnet,str(args.dll)]
    subprocess.run(command+['selftest',str(FIXTURE_PATH)],check=True,timeout=30)
    python_client(command)
    python_master(command)
    print('PASS cross-runtime native protocol in both master/client roles')


if __name__=='__main__':
    main()
