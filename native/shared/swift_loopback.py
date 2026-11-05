"""Exercise the actual Swift NativeSession against the Python oracle on loopback.

Public fixture invitation only. No GUI, Keychain, system clipboard, or input hooks.
"""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
from protocol_reference import (
    MAX_FRAME_BYTES,PREFIX,RecordCipher,ProtocolError,base64_decode,base64_encode,
    derive_keys,frame,parse_frame,seal,validate_hello,
)

ROOT=Path(__file__).resolve().parent
FIXTURE=json.loads((ROOT/'fixtures.json').read_text())
KEY=bytes.fromhex(FIXTURE['keyHex'])
MASTER=FIXTURE['invitePayload']['id']
CLIENT='22222222-2222-4222-8222-222222222222'


def ensure(condition,message):
    if not condition: raise ProtocolError(message)


def run(binary):
    with socket.create_server(('127.0.0.1',0)) as listener:
        listener.settimeout(15)
        port=listener.getsockname()[1]
        process=subprocess.Popen([str(binary),'--protocol-test-client','127.0.0.1',str(port),FIXTURE['invite']],
                                 stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        try:
            conn,_=listener.accept()
            conn.settimeout(10)
            with conn,conn.makefile('rb') as stream:
                read=lambda:parse_frame(stream.readline(MAX_FRAME_BYTES+1))
                challenge=os.urandom(32)
                conn.sendall(frame({'type':'challenge','v':1,'challenge':base64_encode(challenge),'id':MASTER,'name':'Python protocol test master'}))
                hello=validate_hello(read(),KEY,challenge,CLIENT)
                ensure(hello.get('clipboard') is True,'Swift client did not request clipboard capability')
                nonce=base64_decode(hello['clientNonce'],32)
                conn.sendall(frame(seal(KEY,{'type':'welcome','v':1,'challenge':base64_encode(challenge),
                    'clientNonce':base64_encode(nonce),'id':MASTER,'name':'Python protocol test master','clipboard':True},PREFIX+'welcome',0)))
                keys=derive_keys(KEY,challenge,nonce)
                sending,receiving=RecordCipher(keys['s2c'],'s2c'),RecordCipher(keys['c2s'],'c2s')
                while True:
                    payload=receiving.decrypt(read())
                    if payload.get('type')=='ping':
                        conn.sendall(frame(sending.encrypt({'type':'pong'})))
                        continue
                    ensure(payload.get('type')=='clipboard' and payload.get('text')=='Swift native loopback ✅' and
                           payload.get('clock')==1 and payload.get('origin')==CLIENT,'Swift session payload mismatch')
                    break
                conn.sendall(frame(sending.encrypt({'type':'clipboard','text':'CROSS-RUNTIME SERVER ✅\nline 2','clock':2,'origin':MASTER})))
                output,error=process.communicate(timeout=15)
                ensure(process.returncode==0,'Swift protocol client failed: '+error.strip())
                print(output.strip())
            print('PASS actual Swift NativeSession ↔ Python oracle handshake and bidirectional encrypted payloads')
        finally:
            if process.poll() is None:
                process.kill()
                process.communicate(timeout=5)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--binary',type=Path,default=ROOT.parent/'macos/.build/debug/MousharingMac')
    args=parser.parse_args()
    ensure(args.binary.is_file(),'Build the Swift application first')
    run(args.binary.resolve())


if __name__=='__main__': main()
