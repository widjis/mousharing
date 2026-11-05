import base64
import copy
import json
import socket
import threading
import unittest
from pathlib import Path
from protocol_reference import (
    MAX_FRAME_BYTES,PREFIX,ProtocolError,RecordCipher,base64_decode,base64_encode,
    compact,derive_keys,frame,parse_frame,parse_invitation,seal,unseal,
    validate_hello,validate_payload,validate_welcome,
)

FIXTURE=json.loads(Path(__file__).with_name('fixtures.json').read_text())
KEY=bytes.fromhex(FIXTURE['keyHex'])
CHALLENGE=base64_decode(FIXTURE['challengeBase64'],32)
CLIENT_NONCE=base64_decode(FIXTURE['clientNonceBase64'],32)
KEYS=derive_keys(KEY,CHALLENGE,CLIENT_NONCE)
MASTER=FIXTURE['invitePayload']['id']
CLIENT='22222222-2222-4222-8222-222222222222'


class NativeProtocolTests(unittest.TestCase):
    def test_invitation_and_derivation_match_public_vectors(self):
        self.assertEqual(parse_invitation(FIXTURE['invite']),FIXTURE['invitePayload'])
        self.assertEqual(KEYS['c2s'].hex(),FIXTURE['c2sKeyHex'])
        self.assertEqual(KEYS['s2c'].hex(),FIXTURE['s2cKeyHex'])
        self.assertNotEqual(KEYS['c2s'],KEYS['s2c'])

    def test_every_record_matches_encryption_and_decryption_vectors(self):
        for name,record in FIXTURE['records'].items():
            with self.subTest(record=name):
                key=KEY if name in ('hello','welcome') else KEYS['c2s' if name.startswith('c2s') else 's2c']
                plaintext=base64_decode(record['plaintextBase64'])
                envelope=record['envelope']
                actual=seal(key,plaintext,record['aad'],envelope['seq'],base64_decode(envelope['nonce'],12))
                self.assertEqual(actual,envelope)
                self.assertEqual(unseal(key,envelope,record['aad'],envelope['seq']),json.loads(plaintext))

    def test_wrong_key_aad_ciphertext_and_tag_fail(self):
        record=FIXTURE['records']['hello']
        changed_key=bytes([KEY[0]^1])+KEY[1:]
        with self.assertRaises(ProtocolError):
            unseal(changed_key,record['envelope'],record['aad'],0)
        with self.assertRaises(ProtocolError):
            unseal(KEY,record['envelope'],PREFIX+'welcome',0)
        for field in ('ciphertext','tag'):
            changed=copy.deepcopy(record['envelope'])
            raw=bytearray(base64_decode(changed[field])); raw[0]^=1
            changed[field]=base64_encode(raw)
            with self.assertRaises(ProtocolError):
                unseal(KEY,changed,record['aad'],0)

    def test_handshake_binds_nonces_and_identities(self):
        hello=FIXTURE['records']['hello']['envelope']
        welcome=FIXTURE['records']['welcome']['envelope']
        self.assertEqual(validate_hello(hello,KEY,CHALLENGE,CLIENT)['id'],CLIENT)
        self.assertEqual(validate_welcome(welcome,KEY,CHALLENGE,CLIENT_NONCE,MASTER)['id'],MASTER)
        for challenge,client_id in ((bytes(32),CLIENT),(CHALLENGE,MASTER)):
            with self.assertRaises(ProtocolError):
                validate_hello(hello,KEY,challenge,client_id)
        for challenge,nonce,master in ((bytes(32),CLIENT_NONCE,MASTER),(CHALLENGE,bytes(32),MASTER),(CHALLENGE,CLIENT_NONCE,CLIENT)):
            with self.assertRaises(ProtocolError):
                validate_welcome(welcome,KEY,challenge,nonce,master)

    def test_replay_gap_reflection_and_cross_session_records_fail_closed(self):
        sender=RecordCipher(KEYS['c2s'],'c2s')
        first=sender.encrypt({'type':'ping'},nonce=bytes(range(12)))
        receiver=RecordCipher(KEYS['c2s'],'c2s')
        self.assertEqual(receiver.decrypt(first),{'type':'ping'})
        with self.assertRaises(ProtocolError):
            receiver.decrypt(first)
        self.assertTrue(receiver.closed)
        self.assertEqual(receiver.receive_sequence,2)
        with self.assertRaises(ProtocolError):
            receiver.decrypt(sender.encrypt({'type':'ping'}))
        second=sender.encrypt({'type':'ping'})
        fresh=RecordCipher(KEYS['c2s'],'c2s')
        with self.assertRaises(ProtocolError):
            fresh.decrypt(second)
        reflected=RecordCipher(KEYS['s2c'],'s2c')
        with self.assertRaises(ProtocolError):
            reflected.decrypt(first)
        new_keys=derive_keys(KEY,bytes(32),CLIENT_NONCE)
        with self.assertRaises(ProtocolError):
            RecordCipher(new_keys['c2s'],'c2s').decrypt(first)

    def test_failed_auth_does_not_advance_receive_sequence(self):
        changed=copy.deepcopy(FIXTURE['records']['c2sClipboard']['envelope'])
        changed['tag']=base64_encode(bytes(16))
        receiver=RecordCipher(KEYS['c2s'],'c2s')
        with self.assertRaises(ProtocolError): receiver.decrypt(changed)
        self.assertEqual(receiver.receive_sequence,1)
        self.assertTrue(receiver.closed)

    def test_bad_frame_base64_and_invitation_are_rejected(self):
        for raw in (b'{}',b'[]\n',b'\xff\n',b'{"seq":1,"seq":2}\n',b'{"value":NaN}\n',b'x'*(MAX_FRAME_BYTES+1)):
            with self.subTest(length=len(raw)):
                with self.assertRaises(ProtocolError): parse_frame(raw)
        for value in ('!!!!','AA','AA=','AB==','AAA_'):
            with self.assertRaises(ProtocolError): base64_decode(value)
        for value in ('MS1.not an invite','wrong.'+FIXTURE['invite'][4:],FIXTURE['invite']+'=', 'MS1.'+'a'*20000):
            with self.assertRaises(ProtocolError): parse_invitation(value)
        envelope=copy.deepcopy(FIXTURE['records']['hello']['envelope'])
        for sequence in (True,-1,0.0,1):
            envelope['seq']=sequence
            with self.assertRaises(ProtocolError): unseal(KEY,envelope,PREFIX+'hello',0)
        envelope['seq']=0; envelope['nonce']=base64_encode(bytes(11))
        with self.assertRaises(ProtocolError): unseal(KEY,envelope,PREFIX+'hello',0)

    def test_payload_validation_blocks_wrong_direction_and_invalid_data(self):
        enter=json.loads(FIXTURE['records']['s2cEnter']['plaintext'])
        clipboard=json.loads(FIXTURE['records']['c2sClipboard']['plaintext'])
        validate_payload(enter,'s2c'); validate_payload(clipboard,'c2s')
        invalid=[(enter,'c2s'),({'type':'enter','side':'right','ratio':float('nan'),'switchId':1},'s2c'),
                 ({'type':'button','name':'left','down':1},'s2c'),
                 ({'type':'clipboard','text':'x'*(256*1024+1),'clock':1,'origin':CLIENT},'c2s'),
                 ({'type':'clipboard','text':'a\0b','clock':1,'origin':CLIENT},'c2s'),
                 ({'type':'move','dx':float('inf'),'dy':0,'edgeSwitch':True},'s2c')]
        for payload,direction in invalid:
            with self.assertRaises(ProtocolError): validate_payload(payload,direction)

    def test_complete_socket_handshake_and_bidirectional_session(self):
        master,client=socket.socketpair()
        master.settimeout(2); client.settimeout(2)
        errors=[]
        def read(stream): return parse_frame(stream.readline(MAX_FRAME_BYTES+1))
        def server():
            try:
                with master,master.makefile('rb') as stream:
                    master.sendall(frame(FIXTURE['challengePayload']))
                    hello=validate_hello(read(stream),KEY,CHALLENGE,CLIENT)
                    nonce=base64_decode(hello['clientNonce'],32)
                    master.sendall(frame(FIXTURE['records']['welcome']['envelope']))
                    keys=derive_keys(KEY,CHALLENGE,nonce)
                    message=RecordCipher(keys['c2s'],'c2s').decrypt(read(stream))
                    self.assertEqual(message['type'],'clipboard')
                    master.sendall(frame(RecordCipher(keys['s2c'],'s2c').encrypt({'type':'enter','side':'right','ratio':0.75,'switchId':9})))
            except Exception as exc:
                errors.append(exc)
        worker=threading.Thread(target=server,daemon=True); worker.start()
        with client,client.makefile('rb') as stream:
            challenge=read(stream)
            self.assertEqual(challenge['id'],MASTER)
            client.sendall(frame(FIXTURE['records']['hello']['envelope']))
            validate_welcome(read(stream),KEY,CHALLENGE,CLIENT_NONCE,MASTER)
            client.sendall(frame(FIXTURE['records']['c2sClipboard']['envelope']))
            message=RecordCipher(KEYS['s2c'],'s2c').decrypt(read(stream))
            self.assertEqual(message['switchId'],9)
        worker.join(3)
        self.assertFalse(worker.is_alive())
        if errors: raise errors[0]


if __name__=='__main__': unittest.main()
