"""Native v1 interoperability oracle. Test support only; not the GUI runtime."""
from __future__ import annotations
import base64
import hashlib
import json
import os
import uuid
from dataclasses import dataclass
from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.hashes import SHA256
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

MAX_FRAME_BYTES=3*1024*1024
MAX_INVITE_BYTES=16*1024
PREFIX='mousharing-native-v1:'


class ProtocolError(ValueError):
    pass


def compact(value):
    return json.dumps(value,ensure_ascii=False,separators=(',',':'),allow_nan=False).encode('utf-8')


def base64_encode(value):
    return base64.b64encode(value).decode('ascii')


def base64_decode(value,length=None):
    if not isinstance(value,str):
        raise ProtocolError('Invalid base64 type')
    try:
        result=base64.b64decode(value,validate=True)
    except (ValueError,UnicodeError) as exc:
        raise ProtocolError('Invalid base64') from exc
    if base64_encode(result) != value or (length is not None and len(result) != length):
        raise ProtocolError('Invalid base64 length or padding')
    return result


def checked_id(value):
    if not isinstance(value,str):
        raise ProtocolError('Invalid device identity')
    try:
        if str(uuid.UUID(value)) != value:
            raise ProtocolError('Device identity must be lowercase canonical UUID')
    except ValueError as exc:
        raise ProtocolError('Invalid device identity') from exc
    return value


def parse_json(data):
    def object_pairs(pairs):
        value={}
        for key,item in pairs:
            if key in value:
                raise ProtocolError('Duplicate JSON property')
            value[key]=item
        return value
    try:
        value=json.loads(data.decode('utf-8'),object_pairs_hook=object_pairs,
                         parse_constant=lambda _: (_ for _ in ()).throw(ProtocolError('Non-finite JSON number')))
    except (UnicodeError,ValueError) as exc:
        raise ProtocolError('Invalid JSON frame') from exc
    if not isinstance(value,dict):
        raise ProtocolError('Expected object')
    return value


def frame(value):
    result=compact(value)+b'\n'
    if len(result)>MAX_FRAME_BYTES:
        raise ProtocolError('Oversized frame')
    return result


def parse_frame(data):
    if not isinstance(data,bytes) or len(data)>MAX_FRAME_BYTES or not data.endswith(b'\n'):
        raise ProtocolError('Incomplete or oversized frame')
    return parse_json(data[:-1])


def parse_invitation(value):
    if not isinstance(value,str) or not value.startswith('MS1.') or len(value)>MAX_INVITE_BYTES:
        raise ProtocolError('Invalid invitation')
    token=value[4:]
    if any(c not in 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_' for c in token):
        raise ProtocolError('Invalid invitation encoding')
    try:
        data=base64.urlsafe_b64decode(token+'='*((-len(token))%4))
    except ValueError as exc:
        raise ProtocolError('Invalid invitation') from exc
    if base64.urlsafe_b64encode(data).decode().rstrip('=') != token:
        raise ProtocolError('Noncanonical invitation')
    invite=parse_json(data)
    if type(invite.get('v')) is not int or invite['v']!=1:
        raise ProtocolError('Unsupported invitation version')
    checked_id(invite.get('id'))
    base64_decode(invite.get('key'),32)
    if not isinstance(invite.get('name'),str) or not 1<=len(invite['name'])<=128:
        raise ProtocolError('Invalid device name')
    if type(invite.get('port')) is not int or not 1<=invite['port']<=65535:
        raise ProtocolError('Invalid port')
    addresses=invite.get('addresses')
    if not isinstance(addresses,list) or len(addresses)>32 or not all(isinstance(a,str) and len(a)<=64 for a in addresses):
        raise ProtocolError('Invalid addresses')
    return invite


def derive_keys(secret,challenge,client_nonce):
    if any(len(item)!=32 for item in (secret,challenge,client_nonce)):
        raise ProtocolError('Invalid key derivation input length')
    salt=hashlib.sha256(challenge+client_nonce).digest()
    return {direction:HKDF(algorithm=SHA256(),length=32,salt=salt,
                          info=(PREFIX+direction).encode()).derive(secret)
            for direction in ('c2s','s2c')}


def seal(key,payload,aad,sequence,nonce=None):
    nonce=os.urandom(12) if nonce is None else nonce
    if len(key)!=32 or len(nonce)!=12 or type(sequence) is not int or not 0<=sequence<2**63:
        raise ProtocolError('Invalid encryption parameters')
    plaintext=compact(payload) if isinstance(payload,dict) else payload
    encrypted=AESGCM(key).encrypt(nonce,plaintext,aad.encode('utf-8'))
    result={'seq':sequence,'nonce':base64_encode(nonce),'ciphertext':base64_encode(encrypted[:-16]),'tag':base64_encode(encrypted[-16:])}
    frame(result)
    return result


def unseal(key,envelope,aad,expected_sequence):
    if not isinstance(envelope,dict) or type(envelope.get('seq')) is not int or envelope['seq']!=expected_sequence:
        raise ProtocolError('Unexpected record sequence')
    nonce=base64_decode(envelope.get('nonce'),12)
    ciphertext=base64_decode(envelope.get('ciphertext'))
    tag=base64_decode(envelope.get('tag'),16)
    try:
        plaintext=AESGCM(key).decrypt(nonce,ciphertext+tag,aad.encode('utf-8'))
    except (InvalidTag,ValueError) as exc:
        raise ProtocolError('Authentication failed') from exc
    return parse_json(plaintext)


def validate_hello(envelope,secret,challenge,expected_client_id=None):
    hello=unseal(secret,envelope,PREFIX+'hello',0)
    if hello.get('type')!='hello' or type(hello.get('v')) is not int or hello['v']!=1:
        raise ProtocolError('Invalid hello')
    if base64_decode(hello.get('challenge'),32)!=challenge:
        raise ProtocolError('Handshake replay or wrong challenge')
    base64_decode(hello.get('clientNonce'),32)
    checked_id(hello.get('id'))
    if expected_client_id is not None and hello['id']!=expected_client_id:
        raise ProtocolError('Untrusted client identity')
    if not isinstance(hello.get('name'),str) or not 1<=len(hello['name'])<=128:
        raise ProtocolError('Invalid client name')
    return hello


def validate_welcome(envelope,secret,challenge,client_nonce,expected_master_id):
    welcome=unseal(secret,envelope,PREFIX+'welcome',0)
    if welcome.get('type')!='welcome' or type(welcome.get('v')) is not int or welcome['v']!=1:
        raise ProtocolError('Invalid welcome')
    if (base64_decode(welcome.get('challenge'),32)!=challenge or
        base64_decode(welcome.get('clientNonce'),32)!=client_nonce or
        checked_id(welcome.get('id'))!=expected_master_id):
        raise ProtocolError('Welcome identity or nonce mismatch')
    return welcome


@dataclass
class RecordCipher:
    key:bytes
    direction:str
    send_sequence:int=1
    receive_sequence:int=1
    closed:bool=False

    def encrypt(self,payload,nonce=None):
        if self.closed or self.direction not in ('c2s','s2c'):
            raise ProtocolError('Closed or invalid channel')
        envelope=seal(self.key,payload,PREFIX+self.direction+':'+str(self.send_sequence),self.send_sequence,nonce)
        self.send_sequence+=1
        return envelope

    def decrypt(self,envelope):
        if self.closed or self.direction not in ('c2s','s2c'):
            raise ProtocolError('Closed or invalid channel')
        try:
            value=unseal(self.key,envelope,PREFIX+self.direction+':'+str(self.receive_sequence),self.receive_sequence)
        except ProtocolError:
            self.closed=True
            raise
        self.receive_sequence+=1
        return value


def validate_payload(value,direction):
    import math
    if not isinstance(value,dict):
        raise ProtocolError('Expected payload object')
    kind=value.get('type')
    input_types={'enter','move','button','key','scroll','reset'}
    if kind in input_types and direction!='s2c' or kind=='leave' and direction!='c2s':
        raise ProtocolError('Wrong input direction')
    def finite(name):
        item=value.get(name)
        if type(item) not in (int,float) or not math.isfinite(item):
            raise ProtocolError('Invalid numeric input field')
        return item
    if kind in ('enter','leave'):
        ratio=finite('ratio')
        if not 0<=ratio<=1 or type(value.get('switchId')) is not int or value['switchId']<0:
            raise ProtocolError('Invalid transition')
        if kind=='enter' and value.get('side') not in ('left','right','top','bottom'):
            raise ProtocolError('Invalid screen side')
    elif kind in ('move','scroll'):
        finite('dx'); finite('dy')
        if kind=='move' and type(value.get('edgeSwitch')) is not bool:
            raise ProtocolError('Invalid edge switch flag')
    elif kind in ('button','key'):
        if type(value.get('down')) is not bool:
            raise ProtocolError('Invalid pressed state')
        name=value.get('name')
        special={'ctrl','ctrl_r','shift','shift_r','alt','alt_r','cmd','cmd_r','enter','tab','space',
                 'backspace','esc','delete','home','end','page_up','page_down','left','right','up','down','caps_lock'}
        special.update('f'+str(i) for i in range(1,13))
        if kind=='button':
            valid=name in ('left','right','middle')
        else:
            valid=isinstance(name,str) and (name in special or len(name)==1 and name.isprintable() and name.lower()==name)
        if not valid:
            raise ProtocolError('Invalid input name')
    elif kind=='clipboard':
        text=value.get('text')
        if (not isinstance(text,str) or '\0' in text or len(text.encode('utf-8'))>256*1024 or
            type(value.get('clock')) is not int or not 1<=value['clock']<2**63):
            raise ProtocolError('Invalid clipboard payload')
        checked_id(value.get('origin'))
    elif kind not in ('reset','ping','pong'):
        raise ProtocolError('Unsupported message')
    return value
