"""Create a local receiver identity; never overwrite an existing identity."""
import argparse
import datetime
import hashlib
import os
from pathlib import Path


def create_identity(directory):
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    cert_path, key_path = directory / 'windows-cert.pem', directory / 'windows-key.pem'
    if cert_path.exists() or key_path.exists():
        if not cert_path.exists() or not key_path.exists():
            raise SystemExit('Incomplete identity. Restore the matching key/certificate; no files overwritten.')
        # Verify the saved key actually matches before treating setup as complete.
        cert = x509.load_pem_x509_certificate(cert_path.read_bytes())
        key = serialization.load_pem_private_key(key_path.read_bytes(), password=None)
        if cert.public_key().public_numbers() != key.public_key().public_numbers():
            raise SystemExit('Certificate and key do not match; no files overwritten.')
    else:
        key = rsa.generate_private_key(public_exponent=65537, key_size=3072)
        name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'mousharing-windows')])
        now = datetime.datetime.now(datetime.timezone.utc)
        cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
                .public_key(key.public_key()).serial_number(x509.random_serial_number())
                .not_valid_before(now - datetime.timedelta(minutes=5))
                .not_valid_after(now + datetime.timedelta(days=365))
                .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
                .add_extension(x509.SubjectAlternativeName([x509.DNSName('mousharing-windows')]), critical=False)
                .add_extension(x509.KeyUsage(digital_signature=True, content_commitment=False,
                    key_encipherment=True, data_encipherment=False, key_agreement=False,
                    key_cert_sign=True, crl_sign=True, encipher_only=False, decipher_only=False), critical=True)
                .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
                .sign(key, hashes.SHA256()))
        key_data = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                     serialization.NoEncryption())
        fd = os.open(key_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'wb') as stream:
            stream.write(key_data)
        with cert_path.open('xb') as stream:
            stream.write(cert.public_bytes(serialization.Encoding.PEM))
    digest = hashlib.sha256(cert.public_bytes(serialization.Encoding.DER)).hexdigest()
    print('Certificate:', cert_path)
    print('SHA256:', digest)
    print('Copy ONLY windows-cert.pem to Mac. Keep windows-key.pem on Windows.')
    return cert_path, key_path


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', default=str(Path(__file__).resolve().parent.parent / 'data'))
    create_identity(parser.parse_args().directory)
