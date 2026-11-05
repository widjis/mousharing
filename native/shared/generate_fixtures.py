"""Regenerate public deterministic protocol vectors. Never use these secrets live."""
import base64
import hashlib
import json
from pathlib import Path
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.hashes import SHA256
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

b64=lambda data:base64.b64encode(data).decode('ascii')
canonical=lambda value:json.dumps(value,ensure_ascii=False,separators=(',',':')).encode('utf-8')
master_id='11111111-1111-4111-8111-111111111111'
client_id='22222222-2222-4222-8222-222222222222'
key=bytes(range(32))
challenge=bytes(range(32,64))
client_nonce=bytes(range(64,96))
salt=hashlib.sha256(challenge+client_nonce).digest()
keys={direction:HKDF(algorithm=SHA256(),length=32,salt=salt,info=('mousharing-native-v1:'+direction).encode()).derive(key) for direction in ('c2s','s2c')}
invite={'v':1,'id':master_id,'name':'Mac master','port':47832,'key':b64(key),'addresses':['192.0.2.10']}
records={}
def record(name,record_key,nonce,aad,sequence,payload):
    plaintext=canonical(payload)
    encrypted=AESGCM(record_key).encrypt(nonce,plaintext,aad.encode())
    records[name]={'aad':aad,'plaintext':plaintext.decode(),'plaintextBase64':b64(plaintext),
        'envelope':{'seq':sequence,'nonce':b64(nonce),'ciphertext':b64(encrypted[:-16]),'tag':b64(encrypted[-16:])}}
record('hello',key,bytes(range(12)),'mousharing-native-v1:hello',0,
    {'type':'hello','v':1,'challenge':b64(challenge),'clientNonce':b64(client_nonce),'id':client_id,'name':'Windows client'})
record('welcome',key,bytes(range(12,24)),'mousharing-native-v1:welcome',0,
    {'type':'welcome','v':1,'challenge':b64(challenge),'clientNonce':b64(client_nonce),'id':master_id,'name':'Mac master'})
record('c2sClipboard',keys['c2s'],bytes(range(12)),'mousharing-native-v1:c2s:1',1,
    {'type':'clipboard','text':'Halo Mac → Windows 👋\nbaris kedua','clock':1,'origin':client_id})
record('s2cEnter',keys['s2c'],bytes(range(12)),'mousharing-native-v1:s2c:1',1,
    {'type':'enter','side':'right','ratio':0.5,'switchId':7})
fixture={'warning':'PUBLIC TEST SECRETS ONLY. Never pair live devices using these values.',
    'version':1,'keyHex':key.hex(),'keyBase64':b64(key),'challengeBase64':b64(challenge),
    'clientNonceBase64':b64(client_nonce),'saltHex':salt.hex(),
    'c2sKeyHex':keys['c2s'].hex(),'s2cKeyHex':keys['s2c'].hex(),
    'invitePayload':invite,'invite':'MS1.'+base64.urlsafe_b64encode(canonical(invite)).decode().rstrip('='),
    'challengePayload':{'type':'challenge','v':1,'challenge':b64(challenge),'id':master_id,'name':'Mac master'},
    'records':records}
Path(__file__).with_name('fixtures.json').write_text(json.dumps(fixture,ensure_ascii=False,indent=2)+'\n')
